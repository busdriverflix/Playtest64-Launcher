#include "http_sink.h"

#include <stdlib.h>
#include <string.h>

#define BODY_LIMIT (4u * 1024u * 1024u)			// an answer kept in memory; a file is not kept

bool http_sink_put(HttpSink* sink, const u8* data, size_t size)
{
	if (size == 0)
		return true;

	if (sink->file != NULL)
	{
		if (fwrite(data, 1, size, sink->file) != size)
			return false;
	}
	else
	{
		if (sink->memory_size + size + 1 > BODY_LIMIT)
			return false;

		if (sink->memory_size + size + 1 > sink->memory_capacity)
		{
			size_t capacity = sink->memory_capacity == 0 ? 4096 : sink->memory_capacity;

			while (capacity < sink->memory_size + size + 1)
				capacity *= 2;

			char* grown = (char*)realloc(sink->memory, capacity);

			if (grown == NULL)
				return false;

			sink->memory = grown;
			sink->memory_capacity = capacity;
		}

		memcpy(sink->memory + sink->memory_size, data, size);
		sink->memory_size += size;
		sink->memory[sink->memory_size] = '\0';
	}

	sink->written += size;

	if (sink->progress != NULL && !sink->progress(sink->written, sink->total, sink->user))
	{
		sink->cancelled = true;
		return false;
	}

	return true;
}
