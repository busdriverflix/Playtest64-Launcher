/*
	HTTP and HTTPS for Windows, through WinHTTP: part of the system, so nothing is shipped with the
	launcher, and TLS and the system's certificates and proxy settings come with it.
*/

#include "../http_sink.h"

#include <windows.h>
#include <winhttp.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define IO_TIMEOUT_MS 30000
#define READ_CHUNK 65536
#define USER_AGENT L"Playtest64-Launcher/0.1"

/* Functions */

static void say(char* error, size_t size, const char* text, const char* detail)
{
	if (error != NULL && size > 0)
		snprintf(error, size, "%s%s%s", text, detail != NULL ? ": " : "", detail != NULL ? detail : "");
}

static wchar_t* widen(const char* text)
{
	int count = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
	wchar_t* wide = (wchar_t*)malloc((size_t)(count > 0 ? count : 1) * sizeof(wchar_t));

	if (wide != NULL)
	{
		if (count > 0)
			MultiByteToWideChar(CP_UTF8, 0, text, -1, wide, count);
		else
			wide[0] = L'\0';
	}

	return wide;
}

bool http_perform(const HttpRequest* request, HttpResponse* response, char* error, size_t error_size)
{
	memset(response, 0, sizeof(*response));

	HINTERNET session = NULL, connection = NULL, handle = NULL;
	wchar_t* url_wide = widen(request->url);
	wchar_t* method_wide = widen(request->method);
	wchar_t* headers = NULL;
	HttpSink sink = { 0 };
	bool ok = false;

	if (url_wide == NULL || method_wide == NULL)
		goto done;

	wchar_t host[256], path[2048];
	URL_COMPONENTSW parts;

	memset(&parts, 0, sizeof(parts));
	parts.dwStructSize = sizeof(parts);
	parts.lpszHostName = host;
	parts.dwHostNameLength = ARRAYSIZE(host);
	parts.lpszUrlPath = path;
	parts.dwUrlPathLength = ARRAYSIZE(path);

	if (!WinHttpCrackUrl(url_wide, 0, 0, &parts))
	{
		say(error, error_size, "Ungültige Adresse", request->url);
		goto done;
	}

	session = WinHttpOpen(USER_AGENT, WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);

	if (session == NULL)
	{
		say(error, error_size, "WinHTTP lässt sich nicht starten", NULL);
		goto done;
	}

	WinHttpSetTimeouts(session, IO_TIMEOUT_MS, IO_TIMEOUT_MS, IO_TIMEOUT_MS, IO_TIMEOUT_MS);

	connection = WinHttpConnect(session, host, parts.nPort, 0);

	if (connection == NULL)
	{
		say(error, error_size, "Keine Verbindung zum Server", NULL);
		goto done;
	}

	DWORD flags = parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;

	handle = WinHttpOpenRequest(connection, method_wide, path, NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);

	if (handle == NULL)
	{
		say(error, error_size, "Die Anfrage lässt sich nicht aufbauen", NULL);
		goto done;
	}

	// the headers, as one text
	char header_text[2048];
	int length = 0;

	if (request->bearer != NULL)
		length += snprintf(header_text + length, sizeof(header_text) - (size_t)length, "Authorization: Bearer %s\r\n", request->bearer);

	if (request->json_body != NULL)
		length += snprintf(header_text + length, sizeof(header_text) - (size_t)length, "Content-Type: application/json\r\n");

	header_text[length] = '\0';
	headers = widen(header_text);

	size_t body_length = request->json_body != NULL ? strlen(request->json_body) : 0;

	if (!WinHttpSendRequest(handle, length > 0 ? headers : WINHTTP_NO_ADDITIONAL_HEADERS, length > 0 ? (DWORD)-1L : 0,
			(LPVOID)request->json_body, (DWORD)body_length, (DWORD)body_length, 0) ||
		!WinHttpReceiveResponse(handle, NULL))
	{
		say(error, error_size, "Keine Verbindung zum Server", NULL);
		goto done;
	}

	DWORD status = 0, size = sizeof(status);

	WinHttpQueryHeaders(handle, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &size,
		WINHTTP_NO_HEADER_INDEX);
	response->status = (s32)status;

	bool to_file = request->save_path != NULL && status == 200;

	if (to_file)
	{
		sink.file = fopen(request->save_path, "wb");

		if (sink.file == NULL)
		{
			say(error, error_size, "Die Datei lässt sich nicht schreiben", request->save_path);
			goto done;
		}

		wchar_t announced[32];
		DWORD announced_size = sizeof(announced);

		if (WinHttpQueryHeaders(handle, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, announced, &announced_size,
				WINHTTP_NO_HEADER_INDEX))
			sink.total = (u64)_wcstoui64(announced, NULL, 10);

		sink.progress = request->progress;
		sink.user = request->user;
	}

	u8* chunk = (u8*)malloc(READ_CHUNK);
	bool read_ok = chunk != NULL;

	while (read_ok)
	{
		DWORD got = 0;

		if (!WinHttpReadData(handle, chunk, READ_CHUNK, &got))
		{
			read_ok = false;
			break;
		}

		if (got == 0)
			break;

		read_ok = http_sink_put(&sink, chunk, got);
	}

	free(chunk);

	if (sink.file != NULL)
	{
		read_ok = (fclose(sink.file) == 0) && read_ok;
		sink.file = NULL;
	}

	if (!read_ok)
	{
		say(error, error_size, sink.cancelled ? "Abgebrochen" : "Die Antwort des Servers ist abgebrochen", NULL);
		goto done;
	}

	response->body = sink.memory;
	response->size = sink.memory_size;
	response->saved = to_file ? sink.written : 0;
	sink.memory = NULL;
	ok = true;

done:
	if (sink.file != NULL)
		fclose(sink.file);

	free(sink.memory);
	free(url_wide);
	free(method_wide);
	free(headers);

	if (handle != NULL)
		WinHttpCloseHandle(handle);

	if (connection != NULL)
		WinHttpCloseHandle(connection);

	if (session != NULL)
		WinHttpCloseHandle(session);

	return ok;
}

void http_response_free(HttpResponse* response)
{
	free(response->body);
	response->body = NULL;
	response->size = 0;
}
