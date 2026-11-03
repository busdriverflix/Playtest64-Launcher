/*
	One HTTP request and its answer. The launcher needs exactly three things from the server: a login (a
	POST with a JSON body), the list of builds, and a file; so this is no more than that.

	Windows implements it with WinHTTP, which is part of the system. Linux speaks HTTP over a socket
	itself (platform/http_posix.c); HTTPS there needs a TLS library built into the program, which is the
	one piece that is not in yet (see the README).
*/

#ifndef HTTP_H
#define HTTP_H

#include "types.h"

/* Types */

// Called as a download arrives; false cancels it. `total` is 0 when the server did not say.
typedef bool (*HttpProgress)(u64 received, u64 total, void* user);

typedef struct HttpRequest
{
	const char* method;				// "GET" or "POST"
	const char* url;				// http:// or https://
	const char* bearer;				// the login token, or NULL
	const char* json_body;			// for a POST, or NULL
	const char* save_path;			// when set, a 200 answer is written to this file and not kept in memory
	HttpProgress progress;
	void* user;
} HttpRequest;

typedef struct HttpResponse
{
	s32 status;						// 200, 401, ...
	char* body;						// what was answered, zero-terminated (not kept for a download that worked)
	size_t size;
	u64 saved;						// bytes written to save_path
} HttpResponse;

/* Functions */

// False when no answer came at all (no network, no such host, cancelled); `error` says why. An answer
// with a bad status is still an answer: this returns true and the caller reads the status.
bool http_perform(const HttpRequest* request, HttpResponse* response, char* error, size_t error_size);

void http_response_free(HttpResponse* response);

#endif // HTTP_H
