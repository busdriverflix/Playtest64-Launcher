/*
	The small types every file of the launcher uses, named the way the engine names them.

	Author: Felix Röling
*/

#ifndef TYPES_H
#define TYPES_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef uint8_t u8;
typedef uint16_t u16;
typedef uint32_t u32;
typedef uint64_t u64;
typedef int32_t s32;
typedef double f64;

#define ARRAY_COUNT(array) (sizeof(array) / sizeof((array)[0]))

#endif // TYPES_H
