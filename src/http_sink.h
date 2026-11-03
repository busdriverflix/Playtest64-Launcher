/*
	Where the body of an answer goes: into a file when it is a download, into memory when it is something to
	be read. The same on both systems, so it is written once; each system's HTTP code only feeds it.
*/

#ifndef HTTP_SINK_H
#define HTTP_SINK_H

#include "http.h"

#include <stdio.h>

typedef struct HttpSink
{
	FILE* file;						// a download, or NULL
	char* memory;					// an answer, or NULL
	size_t memory_size;
	size_t memory_capacity;
	u64 written;
	u64 total;						// what the server said is coming, or 0
	HttpProgress progress;
	void* user;
	bool cancelled;
} HttpSink;

// False when the write failed, the answer is bigger than the limit for one kept in memory, or the progress callback cancelled
bool http_sink_put(HttpSink* sink, const u8* data, size_t size);

#endif // HTTP_SINK_H
