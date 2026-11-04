/*
	HTTP/1.1 over a plain socket, for Linux. One request per connection ("Connection: close"), which is
	all a launcher that asks three questions needs, and keeps the reading of an answer simple: it ends
	when the length it announced is there, or when the server hangs up.
*/

#define _GNU_SOURCE

#include "../http_sink.h"

#include <ctype.h>
#include <errno.h>
#include <netdb.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

/* Defines */

#define IO_TIMEOUT_SECONDS 30
#define HEADER_LIMIT 16384
#define USER_AGENT "Playtest64-Launcher/0.1"

/* Types */

typedef struct Url
{
	bool secure;
	char host[256];
	char port[8];
	char path[2048];
} Url;

// A connection. TLS goes in here: conn_read and conn_write are the only two places that touch the socket.
typedef struct Conn
{
	int fd;
} Conn;

typedef struct Reader
{
	Conn* conn;
	u8 buffer[16384];
	size_t at;
	size_t size;
	bool eof;
} Reader;

/* Functions */

static void say(char* error, size_t size, const char* text, const char* detail)
{
	if (error != NULL && size > 0)
		snprintf(error, size, "%s%s%s", text, detail != NULL ? ": " : "", detail != NULL ? detail : "");
}

static bool parse_url(const char* text, Url* url)
{
	memset(url, 0, sizeof(*url));

	if (strncmp(text, "https://", 8) == 0)
	{
		url->secure = true;
		text += 8;
	}
	else if (strncmp(text, "http://", 7) == 0)
	{
		text += 7;
	}
	else
	{
		return false;
	}

	const char* slash = strchr(text, '/');
	size_t authority = slash != NULL ? (size_t)(slash - text) : strlen(text);
	const char* colon = memchr(text, ':', authority);
	size_t host_length = colon != NULL ? (size_t)(colon - text) : authority;

	if (host_length == 0 || host_length >= sizeof(url->host))
		return false;

	memcpy(url->host, text, host_length);

	if (colon != NULL)
	{
		size_t port_length = authority - host_length - 1;

		if (port_length == 0 || port_length >= sizeof(url->port))
			return false;

		memcpy(url->port, colon + 1, port_length);
	}
	else
	{
		snprintf(url->port, sizeof(url->port), "%s", url->secure ? "443" : "80");
	}

	snprintf(url->path, sizeof(url->path), "%s", slash != NULL ? slash : "/");

	return true;
}

static bool conn_open(Conn* conn, const Url* url, char* error, size_t error_size)
{
	if (url->secure)
	{
		say(error, error_size, "HTTPS ist in dieser Linux-Version noch nicht eingebaut (siehe README: TLS)", NULL);
		return false;
	}

	struct addrinfo hints;
	struct addrinfo* found = NULL;

	memset(&hints, 0, sizeof(hints));
	hints.ai_family = AF_UNSPEC;
	hints.ai_socktype = SOCK_STREAM;

	if (getaddrinfo(url->host, url->port, &hints, &found) != 0)
	{
		say(error, error_size, "Der Server ist nicht zu finden", url->host);
		return false;
	}

	conn->fd = -1;

	for (struct addrinfo* a = found; a != NULL; a = a->ai_next)
	{
		int fd = socket(a->ai_family, a->ai_socktype, a->ai_protocol);

		if (fd < 0)
			continue;

		struct timeval timeout = { IO_TIMEOUT_SECONDS, 0 };

		setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
		setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));

		if (connect(fd, a->ai_addr, a->ai_addrlen) == 0)
		{
			conn->fd = fd;
			break;
		}

		close(fd);
	}

	freeaddrinfo(found);

	if (conn->fd < 0)
	{
		say(error, error_size, "Keine Verbindung zum Server", url->host);
		return false;
	}

	return true;
}

static void conn_close(Conn* conn)
{
	if (conn->fd >= 0)
		close(conn->fd);

	conn->fd = -1;
}

static bool conn_write(Conn* conn, const void* data, size_t size)
{
	const char* at = (const char*)data;

	while (size > 0)
	{
		ssize_t sent = send(conn->fd, at, size, MSG_NOSIGNAL);

		if (sent <= 0)
		{
			if (sent < 0 && errno == EINTR)
				continue;

			return false;
		}

		at += sent;
		size -= (size_t)sent;
	}

	return true;
}

static ssize_t conn_read(Conn* conn, void* data, size_t size)
{
	for (;;)
	{
		ssize_t got = recv(conn->fd, data, size, 0);

		if (got < 0 && errno == EINTR)
			continue;

		return got;
	}
}

static bool reader_fill(Reader* reader)
{
	if (reader->eof)
		return false;

	ssize_t got = conn_read(reader->conn, reader->buffer, sizeof(reader->buffer));

	if (got <= 0)
	{
		reader->eof = true;
		return false;
	}

	reader->at = 0;
	reader->size = (size_t)got;

	return true;
}

static int reader_byte(Reader* reader)
{
	if (reader->at == reader->size && !reader_fill(reader))
		return -1;

	return reader->buffer[reader->at++];
}

// A line without its line ending. False at the end of the data.
static bool reader_line(Reader* reader, char* line, size_t size)
{
	size_t length = 0;

	for (;;)
	{
		int c = reader_byte(reader);

		if (c < 0)
			return false;

		if (c == '\n')
			break;

		if (length + 1 < size)
			line[length++] = (char)c;
	}

	while (length > 0 && line[length - 1] == '\r')
		length--;

	line[length] = '\0';

	return true;
}

// `length` bytes of the body, or everything until the server hangs up when it is not known
static bool read_plain(Reader* reader, HttpSink* sink, const bool known, u64 length)
{
	for (;;)
	{
		if (known && length == 0)
			return true;

		if (reader->at == reader->size && !reader_fill(reader))
			return !known;			// hanging up is the end of an unannounced body, and a cut-off one otherwise

		size_t available = reader->size - reader->at;
		size_t take = known && length < available ? (size_t)length : available;

		if (!http_sink_put(sink, reader->buffer + reader->at, take))
			return false;

		reader->at += take;
		length -= known ? take : 0;
	}
}

static bool read_chunked(Reader* reader, HttpSink* sink)
{
	char line[64];

	for (;;)
	{
		if (!reader_line(reader, line, sizeof(line)))
			return false;

		char* end;
		u64 size = strtoull(line, &end, 16);

		if (end == line)
			return false;

		if (size == 0)
			return true;

		if (!read_plain(reader, sink, true, size))
			return false;

		// the line break that ends the chunk
		if (!reader_line(reader, line, sizeof(line)))
			return false;
	}
}

bool http_perform(const HttpRequest* request, HttpResponse* response, char* error, size_t error_size)
{
	memset(response, 0, sizeof(*response));

	Url url;

	if (!parse_url(request->url, &url))
	{
		say(error, error_size, "Ungültige Adresse", request->url);
		return false;
	}

	Conn conn = { -1 };

	if (!conn_open(&conn, &url, error, error_size))
		return false;

	char head[4096];
	size_t body_length = request->json_body != NULL ? strlen(request->json_body) : 0;
	int length = snprintf(head, sizeof(head),
		"%s %s HTTP/1.1\r\nHost: %s%s%s\r\nUser-Agent: " USER_AGENT "\r\nAccept: */*\r\nConnection: close\r\n",
		request->method, url.path, url.host, url.secure ? "" : ":", url.secure ? "" : url.port);

	if (request->bearer != NULL)
		length += snprintf(head + length, sizeof(head) - (size_t)length, "Authorization: Bearer %s\r\n", request->bearer);

	if (request->json_body != NULL)
		length += snprintf(head + length, sizeof(head) - (size_t)length,
			"Content-Type: application/json\r\nContent-Length: %zu\r\n", body_length);

	length += snprintf(head + length, sizeof(head) - (size_t)length, "\r\n");

	if (length <= 0 || (size_t)length >= sizeof(head) || !conn_write(&conn, head, (size_t)length) ||
		(body_length > 0 && !conn_write(&conn, request->json_body, body_length)))
	{
		say(error, error_size, "Senden an den Server fehlgeschlagen", NULL);
		conn_close(&conn);
		return false;
	}

	Reader* reader = (Reader*)calloc(1, sizeof(Reader));
	HttpSink sink = { 0 };
	bool ok = false;

	if (reader == NULL)
	{
		conn_close(&conn);
		return false;
	}

	reader->conn = &conn;

	char line[HEADER_LIMIT];

	if (!reader_line(reader, line, sizeof(line)) || strncmp(line, "HTTP/", 5) != 0)
	{
		say(error, error_size, "Der Server hat nicht geantwortet", NULL);
		goto done;
	}

	response->status = atoi(strchr(line, ' ') != NULL ? strchr(line, ' ') + 1 : "0");

	bool chunked = false;
	bool known = false;
	u64 content_length = 0;

	for (;;)
	{
		if (!reader_line(reader, line, sizeof(line)))
		{
			say(error, error_size, "Die Antwort des Servers ist abgebrochen", NULL);
			goto done;
		}

		if (line[0] == '\0')
			break;

		if (strncasecmp(line, "Content-Length:", 15) == 0)
		{
			content_length = strtoull(line + 15, NULL, 10);
			known = true;
		}
		else if (strncasecmp(line, "Transfer-Encoding:", 18) == 0 && strcasestr(line + 18, "chunked") != NULL)
		{
			chunked = true;
		}
	}

	// A good answer to a download goes to the file; anything else (an error in JSON) is kept to be read
	bool to_file = request->save_path != NULL && response->status == 200;

	if (to_file)
	{
		sink.file = fopen(request->save_path, "wb");

		if (sink.file == NULL)
		{
			say(error, error_size, "Die Datei lässt sich nicht schreiben", request->save_path);
			goto done;
		}

		sink.total = known ? content_length : 0;
		sink.progress = request->progress;
		sink.user = request->user;
	}

	bool read = chunked ? read_chunked(reader, &sink) : read_plain(reader, &sink, known, content_length);

	if (sink.file != NULL)
	{
		bool closed = fclose(sink.file) == 0;
		sink.file = NULL;
		read = read && closed;
	}

	if (!read)
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
	free(reader);
	conn_close(&conn);

	return ok;
}

void http_response_free(HttpResponse* response)
{
	free(response->body);
	response->body = NULL;
	response->size = 0;
}
