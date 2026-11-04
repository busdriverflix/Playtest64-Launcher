#include "inflate.h"

#include <setjmp.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define MAX_BITS 15
#define MAX_LIT_CODES 286
#define MAX_DIST_CODES 30
#define FIXED_LIT_CODES 288
#define WINDOW_SIZE 32768			// how far back a match may reach, and the size of what is written at a time
#define INPUT_CHUNK 65536

/* Types */

typedef struct Huffman
{
	u16 count[MAX_BITS + 1];		// how many codes there are of each length
	u16 symbol[FIXED_LIT_CODES];	// the symbols, in the order of their codes
} Huffman;

typedef struct Inflater
{
	FILE* in;
	u64 left;						// compressed bytes not read yet
	u8 buffer[INPUT_CHUNK];
	size_t buffer_at;
	size_t buffer_size;
	u32 bit_buffer;
	u32 bit_count;

	u8 window[WINDOW_SIZE];
	u64 total;						// bytes put out so far
	InflateWrite write;
	void* user;

	jmp_buf failure;
} Inflater;

/* Variables */

static const u16 LENGTH_BASE[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
static const u16 LENGTH_EXTRA[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
static const u16 DIST_BASE[30] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
static const u16 DIST_EXTRA[30] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
static const u8 CODE_LENGTH_ORDER[19] = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

/* Functions */

u32 crc32_update(u32 crc, const u8* data, size_t size)
{
	static u32 table[256];
	static bool made = false;

	if (!made)
	{
		for (u32 i = 0; i < 256; i++)
		{
			u32 c = i;

			for (u32 k = 0; k < 8; k++)
				c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;

			table[i] = c;
		}

		made = true;
	}

	crc = ~crc;

	for (size_t i = 0; i < size; i++)
		crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

	return ~crc;
}

static void fail(Inflater* state)
{
	longjmp(state->failure, 1);
}

static u32 next_byte(Inflater* state)
{
	if (state->buffer_at == state->buffer_size)
	{
		size_t want = state->left < INPUT_CHUNK ? (size_t)state->left : INPUT_CHUNK;

		if (want == 0)
			fail(state);

		state->buffer_size = fread(state->buffer, 1, want, state->in);
		state->buffer_at = 0;

		if (state->buffer_size == 0)
			fail(state);

		state->left -= state->buffer_size;
	}

	return state->buffer[state->buffer_at++];
}

static u32 get_bits(Inflater* state, const u32 need)
{
	while (state->bit_count < need)
	{
		state->bit_buffer |= next_byte(state) << state->bit_count;
		state->bit_count += 8;
	}

	u32 value = state->bit_buffer & ((1u << need) - 1u);

	state->bit_buffer >>= need;
	state->bit_count -= need;

	return value;
}

static void flush_window(Inflater* state, const size_t size)
{
	// the start of what is not written yet: a whole window back from a full one, otherwise the beginning
	if (size > 0 && !state->write(state->window, size, state->user))
		fail(state);
}

static void put_byte(Inflater* state, const u8 byte)
{
	state->window[state->total & (WINDOW_SIZE - 1)] = byte;
	state->total++;

	if ((state->total & (WINDOW_SIZE - 1)) == 0)
		flush_window(state, WINDOW_SIZE);
}

// Builds the decoding tables for a set of code lengths. Zero when the code is complete, negative
// when it has more codes than the lengths allow, positive when it leaves some unused (legal for a
// stream with a single distance code, and for nothing else here).
static s32 construct(Huffman* h, const u16* length, const u32 n)
{
	u16 offsets[MAX_BITS + 1];

	memset(h->count, 0, sizeof(h->count));

	for (u32 symbol = 0; symbol < n; symbol++)
		h->count[length[symbol]]++;

	if (h->count[0] == n)
		return 0;

	s32 left = 1;

	for (u32 len = 1; len <= MAX_BITS; len++)
	{
		left <<= 1;
		left -= h->count[len];

		if (left < 0)
			return left;
	}

	offsets[1] = 0;

	for (u32 len = 1; len < MAX_BITS; len++)
		offsets[len + 1] = (u16)(offsets[len] + h->count[len]);

	for (u32 symbol = 0; symbol < n; symbol++)
	{
		if (length[symbol] != 0)
			h->symbol[offsets[length[symbol]]++] = (u16)symbol;
	}

	return left;
}

static u32 decode(Inflater* state, const Huffman* h)
{
	s32 code = 0, first = 0, index = 0;

	for (u32 len = 1; len <= MAX_BITS; len++)
	{
		code |= (s32)get_bits(state, 1);

		s32 count = h->count[len];

		if (code - count < first)
			return h->symbol[index + (code - first)];

		index += count;
		first += count;
		first <<= 1;
		code <<= 1;
	}

	fail(state);

	return 0;
}

static void codes(Inflater* state, const Huffman* lengths, const Huffman* distances)
{
	for (;;)
	{
		u32 symbol = decode(state, lengths);

		if (symbol < 256)
		{
			put_byte(state, (u8)symbol);
			continue;
		}

		if (symbol == 256)
			return;

		symbol -= 257;

		if (symbol >= 29)
			fail(state);

		u32 length = LENGTH_BASE[symbol] + get_bits(state, LENGTH_EXTRA[symbol]);
		u32 dist_symbol = decode(state, distances);

		if (dist_symbol >= 30)
			fail(state);

		u32 distance = DIST_BASE[dist_symbol] + get_bits(state, DIST_EXTRA[dist_symbol]);

		if (distance > state->total)
			fail(state);

		while (length-- > 0)
			put_byte(state, state->window[(state->total - distance) & (WINDOW_SIZE - 1)]);
	}
}

static void stored_block(Inflater* state)
{
	state->bit_buffer = 0;
	state->bit_count = 0;

	u32 length = next_byte(state);
	length |= next_byte(state) << 8;

	u32 inverse = next_byte(state);
	inverse |= next_byte(state) << 8;

	if (length != (~inverse & 0xFFFF))
		fail(state);

	while (length-- > 0)
		put_byte(state, (u8)next_byte(state));
}

static void fixed_block(Inflater* state)
{
	static Huffman lengths, distances;
	static bool built = false;

	if (!built)
	{
		u16 length[FIXED_LIT_CODES];
		u32 symbol = 0;

		for (; symbol < 144; symbol++) length[symbol] = 8;
		for (; symbol < 256; symbol++) length[symbol] = 9;
		for (; symbol < 280; symbol++) length[symbol] = 7;
		for (; symbol < FIXED_LIT_CODES; symbol++) length[symbol] = 8;

		construct(&lengths, length, FIXED_LIT_CODES);

		for (symbol = 0; symbol < MAX_DIST_CODES; symbol++)
			length[symbol] = 5;

		construct(&distances, length, MAX_DIST_CODES);
		built = true;
	}

	codes(state, &lengths, &distances);
}

static void dynamic_block(Inflater* state)
{
	u16 lengths[MAX_LIT_CODES + MAX_DIST_CODES];
	Huffman lit, dist;

	u32 lit_count = get_bits(state, 5) + 257;
	u32 dist_count = get_bits(state, 5) + 1;
	u32 code_count = get_bits(state, 4) + 4;

	if (lit_count > MAX_LIT_CODES || dist_count > MAX_DIST_CODES)
		fail(state);

	u32 index = 0;

	for (; index < code_count; index++)
		lengths[CODE_LENGTH_ORDER[index]] = (u16)get_bits(state, 3);

	for (; index < 19; index++)
		lengths[CODE_LENGTH_ORDER[index]] = 0;

	if (construct(&lit, lengths, 19) != 0)
		fail(state);

	index = 0;

	while (index < lit_count + dist_count)
	{
		u32 symbol = decode(state, &lit);

		if (symbol < 16)
		{
			lengths[index++] = (u16)symbol;
			continue;
		}

		u16 repeated = 0;
		u32 repeat;

		if (symbol == 16)
		{
			if (index == 0)
				fail(state);

			repeated = lengths[index - 1];
			repeat = 3 + get_bits(state, 2);
		}
		else if (symbol == 17)
		{
			repeat = 3 + get_bits(state, 3);
		}
		else
		{
			repeat = 11 + get_bits(state, 7);
		}

		if (index + repeat > lit_count + dist_count)
			fail(state);

		while (repeat-- > 0)
			lengths[index++] = repeated;
	}

	if (lengths[256] == 0)
		fail(state);

	s32 left = construct(&lit, lengths, lit_count);

	if (left < 0 || (left > 0 && lit_count - lit.count[0] != 1))
		fail(state);

	left = construct(&dist, lengths + lit_count, dist_count);

	if (left < 0 || (left > 0 && dist_count - dist.count[0] != 1))
		fail(state);

	codes(state, &lit, &dist);
}

bool inflate_stream(FILE* in, u64 compressed_size, InflateWrite write, void* user)
{
	Inflater* state = (Inflater*)calloc(1, sizeof(Inflater));

	if (state == NULL)
		return false;

	state->in = in;
	state->left = compressed_size;
	state->write = write;
	state->user = user;

	bool ok = false;

	if (setjmp(state->failure) == 0)
	{
		u32 last;

		do
		{
			last = get_bits(state, 1);

			switch (get_bits(state, 2))
			{
			case 0: stored_block(state); break;
			case 1: fixed_block(state); break;
			case 2: dynamic_block(state); break;
			default: fail(state);
			}
		}
		while (!last);

		// what is left in the window since it was last written out
		flush_window(state, (size_t)(state->total & (WINDOW_SIZE - 1)));
		ok = true;
	}

	free(state);

	return ok;
}
