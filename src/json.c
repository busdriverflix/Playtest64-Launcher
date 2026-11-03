#include "json.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define JSON_MAX_DEPTH 32			// nesting past this is not a reply from our server

/* Types */

typedef struct Parser
{
	const char* at;
	u32 depth;
	bool failed;
} Parser;

/* Functions */

static void skip_space(Parser* parser)
{
	while (*parser->at == ' ' || *parser->at == '\t' || *parser->at == '\r' || *parser->at == '\n')
		parser->at++;
}

static JsonValue* add_child(JsonValue* parent)
{
	if (parent->count == parent->capacity)
	{
		u32 capacity = parent->capacity == 0 ? 4 : parent->capacity * 2;
		JsonValue* grown = (JsonValue*)realloc(parent->children, capacity * sizeof(JsonValue));

		if (grown == NULL)
			return NULL;

		parent->children = grown;
		parent->capacity = capacity;
	}

	JsonValue* child = &parent->children[parent->count++];
	memset(child, 0, sizeof(*child));

	return child;
}

static void put_utf8(char** out, u32 code)
{
	if (code < 0x80)
	{
		*(*out)++ = (char)code;
	}
	else if (code < 0x800)
	{
		*(*out)++ = (char)(0xC0 | (code >> 6));
		*(*out)++ = (char)(0x80 | (code & 0x3F));
	}
	else if (code < 0x10000)
	{
		*(*out)++ = (char)(0xE0 | (code >> 12));
		*(*out)++ = (char)(0x80 | ((code >> 6) & 0x3F));
		*(*out)++ = (char)(0x80 | (code & 0x3F));
	}
	else
	{
		*(*out)++ = (char)(0xF0 | (code >> 18));
		*(*out)++ = (char)(0x80 | ((code >> 12) & 0x3F));
		*(*out)++ = (char)(0x80 | ((code >> 6) & 0x3F));
		*(*out)++ = (char)(0x80 | (code & 0x3F));
	}
}

static bool read_hex4(const char* at, u32* code)
{
	*code = 0;

	for (u32 i = 0; i < 4; i++)
	{
		char c = at[i];
		u32 digit;

		if (c >= '0' && c <= '9')
			digit = (u32)(c - '0');
		else if (c >= 'a' && c <= 'f')
			digit = (u32)(c - 'a') + 10;
		else if (c >= 'A' && c <= 'F')
			digit = (u32)(c - 'A') + 10;
		else
			return false;

		*code = (*code << 4) | digit;
	}

	return true;
}

// A string literal, the opening quote already seen. The text can only get shorter than the source.
static char* parse_string(Parser* parser)
{
	const char* start = parser->at;
	const char* end = start;

	while (*end != '"')
	{
		if (*end == '\0')
		{
			parser->failed = true;
			return NULL;
		}

		end += (*end == '\\' && end[1] != '\0') ? 2 : 1;
	}

	char* text = (char*)malloc((size_t)(end - start) + 1);

	if (text == NULL)
	{
		parser->failed = true;
		return NULL;
	}

	char* out = text;

	for (const char* in = start; in < end; in++)
	{
		if (*in != '\\')
		{
			*out++ = *in;
			continue;
		}

		in++;

		switch (*in)
		{
		case 'n': *out++ = '\n'; break;
		case 't': *out++ = '\t'; break;
		case 'r': *out++ = '\r'; break;
		case 'b': *out++ = '\b'; break;
		case 'f': *out++ = '\f'; break;
		case 'u':
		{
			u32 code;

			if (end - in < 5 || !read_hex4(in + 1, &code))
			{
				free(text);
				parser->failed = true;
				return NULL;
			}

			in += 4;

			// a pair of surrogates is one character
			if (code >= 0xD800 && code < 0xDC00 && end - in >= 7 && in[1] == '\\' && in[2] == 'u')
			{
				u32 low;

				if (read_hex4(in + 3, &low) && low >= 0xDC00 && low < 0xE000)
				{
					code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
					in += 6;
				}
			}

			put_utf8(&out, code);
			break;
		}
		default: *out++ = *in; break;			// \" \\ \/ and anything odd stands for itself
		}
	}

	*out = '\0';
	parser->at = end + 1;

	return text;
}

static bool parse_value(Parser* parser, JsonValue* value)
{
	skip_space(parser);

	if (parser->depth >= JSON_MAX_DEPTH)
		return false;

	char c = *parser->at;

	if (c == '"')
	{
		parser->at++;
		value->type = JSON_STRING;
		value->string = parse_string(parser);
		return !parser->failed;
	}

	if (c == '{' || c == '[')
	{
		const bool object = c == '{';
		const char close = object ? '}' : ']';

		parser->at++;
		parser->depth++;
		value->type = object ? JSON_OBJECT : JSON_ARRAY;
		skip_space(parser);

		if (*parser->at == close)
		{
			parser->at++;
			parser->depth--;
			return true;
		}

		for (;;)
		{
			JsonValue* child = add_child(value);

			if (child == NULL)
				return false;

			if (object)
			{
				skip_space(parser);

				if (*parser->at != '"')
					return false;

				parser->at++;
				child->key = parse_string(parser);

				if (child->key == NULL)
					return false;

				skip_space(parser);

				if (*parser->at != ':')
					return false;

				parser->at++;
			}

			if (!parse_value(parser, child))
				return false;

			skip_space(parser);

			if (*parser->at == ',')
			{
				parser->at++;
				continue;
			}

			if (*parser->at == close)
			{
				parser->at++;
				parser->depth--;
				return true;
			}

			return false;
		}
	}

	if (strncmp(parser->at, "true", 4) == 0)
	{
		value->type = JSON_BOOL;
		value->boolean = true;
		parser->at += 4;
		return true;
	}

	if (strncmp(parser->at, "false", 5) == 0)
	{
		value->type = JSON_BOOL;
		parser->at += 5;
		return true;
	}

	if (strncmp(parser->at, "null", 4) == 0)
	{
		value->type = JSON_NULL;
		parser->at += 4;
		return true;
	}

	if (c == '-' || (c >= '0' && c <= '9'))
	{
		char* end;
		value->type = JSON_NUMBER;
		value->number = strtod(parser->at, &end);

		if (end == parser->at)
			return false;

		parser->at = end;
		return true;
	}

	return false;
}

JsonValue* json_parse(const char* text)
{
	if (text == NULL)
		return NULL;

	JsonValue* root = (JsonValue*)calloc(1, sizeof(JsonValue));

	if (root == NULL)
		return NULL;

	Parser parser = { text, 0, false };

	if (!parse_value(&parser, root))
	{
		json_free(root);
		return NULL;
	}

	skip_space(&parser);

	if (*parser.at != '\0')
	{
		json_free(root);
		return NULL;
	}

	return root;
}

static void free_contents(JsonValue* value)
{
	for (u32 i = 0; i < value->count; i++)
		free_contents(&value->children[i]);

	free(value->children);
	free(value->string);
	free(value->key);
}

void json_free(JsonValue* value)
{
	if (value == NULL)
		return;

	free_contents(value);
	free(value);
}

const JsonValue* json_member(const JsonValue* object, const char* key)
{
	if (object == NULL || object->type != JSON_OBJECT)
		return NULL;

	for (u32 i = 0; i < object->count; i++)
	{
		if (object->children[i].key != NULL && strcmp(object->children[i].key, key) == 0)
			return &object->children[i];
	}

	return NULL;
}

const JsonValue* json_item(const JsonValue* array, const u32 index)
{
	if (array == NULL || array->type != JSON_ARRAY || index >= array->count)
		return NULL;

	return &array->children[index];
}

u32 json_count(const JsonValue* value)
{
	return value != NULL && (value->type == JSON_ARRAY || value->type == JSON_OBJECT) ? value->count : 0;
}

const char* json_text(const JsonValue* value, const char* fallback)
{
	return value != NULL && value->type == JSON_STRING && value->string != NULL ? value->string : fallback;
}

f64 json_number(const JsonValue* value, const f64 fallback)
{
	return value != NULL && value->type == JSON_NUMBER ? value->number : fallback;
}

bool json_flag(const JsonValue* value, const bool fallback)
{
	return value != NULL && value->type == JSON_BOOL ? value->boolean : fallback;
}

bool json_quote(const char* text, char* out, size_t size)
{
	size_t used = 0;

	if (size < 3)
		return false;

	out[used++] = '"';

	for (const char* c = text; *c != '\0'; c++)
	{
		char escaped[8];
		size_t length;

		switch (*c)
		{
		case '"': memcpy(escaped, "\\\"", 2); length = 2; break;
		case '\\': memcpy(escaped, "\\\\", 2); length = 2; break;
		case '\n': memcpy(escaped, "\\n", 2); length = 2; break;
		case '\r': memcpy(escaped, "\\r", 2); length = 2; break;
		case '\t': memcpy(escaped, "\\t", 2); length = 2; break;
		default:
			if ((unsigned char)*c < 0x20)
			{
				length = (size_t)snprintf(escaped, sizeof(escaped), "\\u%04x", (unsigned)*c);
			}
			else
			{
				escaped[0] = *c;
				length = 1;
			}
			break;
		}

		if (used + length + 2 > size)
			return false;

		memcpy(out + used, escaped, length);
		used += length;
	}

	out[used++] = '"';
	out[used] = '\0';

	return true;
}
