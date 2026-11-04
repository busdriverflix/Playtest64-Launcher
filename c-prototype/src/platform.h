/*
	What differs between Windows and Linux, in one place: everything above this line of functions is the
	same code on both. One implementation file per system sits in platform/.
*/

#ifndef PLATFORM_H
#define PLATFORM_H

#include "types.h"

/* Functions */

// "Windows" or "Linux": the names the website gives the platforms of a build
const char* platform_name(void);

// Where the launcher keeps its settings, its downloads and the installed builds. Made if it is not there.
// The environment variable PLAYTEST_LAUNCHER_HOME moves it (the tests use that).
bool platform_data_dir(char* out, size_t size);

// The path of this program
bool platform_self_path(char* out, size_t size);

bool platform_mkdir_p(const char* path);
bool platform_file_exists(const char* path);
// A file that was unpacked from a Linux zip and was meant to be run; a no-op on Windows
bool platform_make_executable(const char* path);
// Moves a file over another one that may exist
bool platform_replace_file(const char* from, const char* to);

// Starts a program on its own, in a folder of its own, and does not wait for it
bool platform_launch(const char* program, const char* working_dir);

// A message on the desktop. Where there is no desktop to show it on it is printed instead.
void platform_notify(const char* title, const char* text);

// Whether the background check starts with the system or the login, and putting that on or off
bool platform_autostart_get(void);
bool platform_autostart_set(const bool on);

void platform_sleep_seconds(const u32 seconds);

#endif // PLATFORM_H
