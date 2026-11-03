/*
	A JSON reader, as small as what the launcher asks of the server needs: the text goes in, a tree of
	values comes out, and the accessors give back a fallback for anything that is not there or not of
	the type asked for, so a reply from a newer server never crashes an older launcher.
*/

#ifndef JSON_H
#define JSON_H

#include "types.h"

typedef enum JsonType
{
	JSON_NULL,
	JSON_BOOL,
	JSON_NUMBER,
	JSON_STRING,
	JSON_ARRAY,
	JSON_OBJECT,
} JsonType;

typedef struct JsonValue JsonValue;

struct JsonValue
{
	JsonType type;
	bool boolean;
	f64 number;
	char* string;				// a string's text
	char* key;					// the name this value has in its object, NULL anywhere else
	JsonValue* children;		// an array's items or an object's members
	u32 count;
	u32 capacity;
};

// NULL when the text is not JSON. Whoever gets the tree gives it back with json_free.
JsonValue* json_parse(const char* text);
void json_free(JsonValue* value);

const JsonValue* json_member(const JsonValue* object, const char* key);
const JsonValue* json_item(const JsonValue* array, const u32 index);
u32 json_count(const JsonValue* value);

const char* json_text(const JsonValue* value, const char* fallback);
f64 json_number(const JsonValue* value, const f64 fallback);
bool json_flag(const JsonValue* value, const bool fallback);

// A JSON string literal for the text, quotes included, written into out: for building a request body
bool json_quote(const char* text, char* out, size_t size);

#endif // JSON_H
