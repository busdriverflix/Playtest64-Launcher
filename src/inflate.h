/*
	Deflate, the compression inside a zip: the one thing a download of a build needs that the system does
	not hand over on every machine. Written after Mark Adler's "puff" (the readable reference inflater
	that comes with zlib), made to stream: what comes out goes to a callback a block at a time, so a
	build of a few hundred megabytes is never in memory at once.
*/

#ifndef INFLATE_H
#define INFLATE_H

#include "types.h"

#include <stdio.h>

typedef bool (*InflateWrite)(const u8* data, size_t size, void* user);

// Reads `compressed_size` bytes of a raw deflate stream from the file's current position and hands
// the unpacked data to `write`. False on a damaged stream, a read that ran short, or `write` refusing.
bool inflate_stream(FILE* in, u64 compressed_size, InflateWrite write, void* user);

u32 crc32_update(u32 crc, const u8* data, size_t size);

#endif // INFLATE_H
