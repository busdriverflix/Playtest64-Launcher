#include "zip.h"
#include "inflate.h"
#include "platform.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define EOCD_SIGNATURE 0x06054B50u
#define CENTRAL_SIGNATURE 0x02014B50u
#define LOCAL_SIGNATURE 0x04034B50u
#define EOCD_SIZE 22
#define CENTRAL_SIZE 46
#define LOCAL_SIZE 30
#define PATH_LIMIT 1024

/* Types */

typedef struct Sink
{
	FILE* out;
	u32 crc;
	u64 written;
} Sink;

/* Functions */

static u32 read_u16(const u8* at)
{
	return (u32)at[0] | ((u32)at[1] << 8);
}

static u32 read_u32(const u8* at)
{
	return read_u16(at) | (read_u16(at + 2) << 16);
}

static void say(char* error, size_t size, const char* text, const char* detail)
{
	if (error != NULL && size > 0)
		snprintf(error, size, "%s%s%s", text, detail != NULL ? ": " : "", detail != NULL ? detail : "");
}

static bool sink_write(const u8* data, size_t size, void* user)
{
	Sink* sink = (Sink*)user;

	if (fwrite(data, 1, size, sink->out) != size)
		return false;

	sink->crc = crc32_update(sink->crc, data, size);
	sink->written += size;

	return true;
}

// Whether a name from the archive stays inside the folder it is unpacked into
static bool name_is_safe(const char* name)
{
	if (name[0] == '/' || name[0] == '\\' || (name[0] != '\0' && name[1] == ':'))
		return false;

	const char* part = name;

	while (*part != '\0')
	{
		const char* end = part;

		while (*end != '\0' && *end != '/')
			end++;

		if (end - part == 2 && part[0] == '.' && part[1] == '.')
			return false;

		part = *end == '/' ? end + 1 : end;
	}

	return true;
}

// The parent folders of a file path, made
static bool make_parents(char* path)
{
	for (char* c = path + 1; *c != '\0'; c++)
	{
		if (*c != '/')
			continue;

		*c = '\0';
		bool made = platform_mkdir_p(path);
		*c = '/';

		if (!made)
			return false;
	}

	return true;
}

bool zip_extract(const char* zip_path, const char* dest_dir, char* top, size_t top_size, char* error, size_t error_size)
{
	if (top != NULL && top_size > 0)
		top[0] = '\0';

	FILE* file = fopen(zip_path, "rb");

	if (file == NULL)
	{
		say(error, error_size, "Die Zip-Datei lässt sich nicht öffnen", zip_path);
		return false;
	}

	bool ok = false;
	u8* directory = NULL;

	// the end-of-directory record is somewhere in the last 64 KB
	fseek(file, 0, SEEK_END);
	long file_size = ftell(file);
	long tail_size = file_size < 65557 ? file_size : 65557;
	u8* tail = (u8*)malloc((size_t)tail_size);

	if (tail == NULL || file_size < EOCD_SIZE)
	{
		say(error, error_size, "Das ist keine Zip-Datei", NULL);
		free(tail);
		fclose(file);
		return false;
	}

	fseek(file, file_size - tail_size, SEEK_SET);

	if (fread(tail, 1, (size_t)tail_size, file) != (size_t)tail_size)
	{
		say(error, error_size, "Die Zip-Datei lässt sich nicht lesen", NULL);
		free(tail);
		fclose(file);
		return false;
	}

	long eocd = -1;

	for (long i = tail_size - EOCD_SIZE; i >= 0; i--)
	{
		if (read_u32(tail + i) == EOCD_SIGNATURE)
		{
			eocd = i;
			break;
		}
	}

	if (eocd < 0)
	{
		say(error, error_size, "Das ist keine Zip-Datei", NULL);
		free(tail);
		fclose(file);
		return false;
	}

	u32 entry_count = read_u16(tail + eocd + 10);
	u32 directory_size = read_u32(tail + eocd + 12);
	u32 directory_offset = read_u32(tail + eocd + 16);

	free(tail);

	if (entry_count == 0xFFFF || directory_size == 0xFFFFFFFFu || directory_offset == 0xFFFFFFFFu)
	{
		say(error, error_size, "Zip64 wird nicht unterstützt", NULL);
		fclose(file);
		return false;
	}

	directory = (u8*)malloc(directory_size > 0 ? directory_size : 1);

	if (directory == NULL || fseek(file, (long)directory_offset, SEEK_SET) != 0 ||
		fread(directory, 1, directory_size, file) != directory_size)
	{
		say(error, error_size, "Das Verzeichnis der Zip-Datei ist beschädigt", NULL);
		goto done;
	}

	size_t at = 0;
	bool first = true;

	for (u32 entry = 0; entry < entry_count; entry++)
	{
		if (at + CENTRAL_SIZE > directory_size || read_u32(directory + at) != CENTRAL_SIGNATURE)
		{
			say(error, error_size, "Das Verzeichnis der Zip-Datei ist beschädigt", NULL);
			goto done;
		}

		const u8* record = directory + at;
		u32 flags = read_u16(record + 8);
		u32 method = read_u16(record + 10);
		u32 crc = read_u32(record + 16);
		u32 compressed = read_u32(record + 20);
		u32 size = read_u32(record + 24);
		u32 name_length = read_u16(record + 28);
		u32 extra_length = read_u16(record + 30);
		u32 comment_length = read_u16(record + 32);
		u32 made_by = read_u16(record + 4) >> 8;
		u32 external = read_u32(record + 38);
		u32 local_offset = read_u32(record + 42);

		if (at + CENTRAL_SIZE + name_length > directory_size || name_length >= PATH_LIMIT / 2)
		{
			say(error, error_size, "Das Verzeichnis der Zip-Datei ist beschädigt", NULL);
			goto done;
		}

		at += CENTRAL_SIZE + name_length + extra_length + comment_length;

		char name[PATH_LIMIT / 2];
		memcpy(name, record + CENTRAL_SIZE, name_length);
		name[name_length] = '\0';

		for (char* c = name; *c != '\0'; c++)
		{
			if (*c == '\\')
				*c = '/';
		}

		if (!name_is_safe(name))
		{
			say(error, error_size, "Die Zip-Datei enthält einen unzulässigen Pfad", name);
			goto done;
		}

		if (first && top != NULL)
		{
			const char* slash = strchr(name, '/');

			if (slash != NULL && (size_t)(slash - name) < top_size)
			{
				memcpy(top, name, (size_t)(slash - name));
				top[slash - name] = '\0';
			}
		}

		first = false;

		if (flags & 1)
		{
			say(error, error_size, "Verschlüsselte Zip-Dateien werden nicht unterstützt", NULL);
			goto done;
		}

		if (compressed == 0xFFFFFFFFu || size == 0xFFFFFFFFu || local_offset == 0xFFFFFFFFu)
		{
			say(error, error_size, "Zip64 wird nicht unterstützt", NULL);
			goto done;
		}

		char path[PATH_LIMIT];
		snprintf(path, sizeof(path), "%s/%s", dest_dir, name);

		if (name_length > 0 && name[name_length - 1] == '/')
		{
			path[strlen(path) - 1] = '\0';

			if (!platform_mkdir_p(path))
			{
				say(error, error_size, "Ein Ordner lässt sich nicht anlegen", path);
				goto done;
			}

			continue;
		}

		u8 local[LOCAL_SIZE];

		if (fseek(file, (long)local_offset, SEEK_SET) != 0 || fread(local, 1, LOCAL_SIZE, file) != LOCAL_SIZE ||
			read_u32(local) != LOCAL_SIGNATURE)
		{
			say(error, error_size, "Ein Eintrag der Zip-Datei ist beschädigt", name);
			goto done;
		}

		long data_offset = (long)local_offset + LOCAL_SIZE + (long)read_u16(local + 26) + (long)read_u16(local + 28);

		if (fseek(file, data_offset, SEEK_SET) != 0 || !make_parents(path))
		{
			say(error, error_size, "Ein Ordner lässt sich nicht anlegen", path);
			goto done;
		}

		Sink sink = { fopen(path, "wb"), 0, 0 };

		if (sink.out == NULL)
		{
			say(error, error_size, "Eine Datei lässt sich nicht schreiben", path);
			goto done;
		}

		bool unpacked;

		if (method == 0)
		{
			u8 chunk[16384];
			u32 left = compressed;
			unpacked = true;

			while (left > 0 && unpacked)
			{
				size_t want = left < sizeof(chunk) ? left : sizeof(chunk);
				unpacked = fread(chunk, 1, want, file) == want && sink_write(chunk, want, &sink);
				left -= (u32)want;
			}
		}
		else if (method == 8)
		{
			unpacked = inflate_stream(file, compressed, sink_write, &sink);
		}
		else
		{
			fclose(sink.out);
			say(error, error_size, "Unbekanntes Packverfahren", name);
			goto done;
		}

		bool closed = fclose(sink.out) == 0;

		if (!unpacked || !closed || sink.written != size || sink.crc != crc)
		{
			say(error, error_size, "Eine Datei ist beschädigt oder konnte nicht entpackt werden", name);
			goto done;
		}

		// made on a Unix system: the permissions are in the top half of the external attributes
		if (made_by == 3 && ((external >> 16) & 0100) != 0)
			platform_make_executable(path);
	}

	ok = true;

done:
	free(directory);
	fclose(file);

	return ok;
}
