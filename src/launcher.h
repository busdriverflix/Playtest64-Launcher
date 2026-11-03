/*
	What the launcher does, with no window and no system call of its own: signing in, asking which builds
	there are, installing one, starting it, and noticing that a new one has appeared. The user interfaces
	(a window on Windows, text on Linux) and the background check are all callers of this.

	Everything the launcher keeps is in one folder (platform_data_dir):

	  launcher.cfg                 the server, who is signed in and the token, which build was last announced
	  downloads/                   a zip while it comes in
	  builds/<version>/            an installed build, with installed.txt in it once it is complete
*/

#ifndef LAUNCHER_H
#define LAUNCHER_H

#include "types.h"
#include "http.h"

/* Defines */

#define LAUNCHER_VERSION "0.1.0"
#define LAUNCHER_DEFAULT_SERVER "https://playtest64.xyz"
#define LAUNCHER_MAX_BUILDS 64
#define LAUNCHER_PATH 512
#define LAUNCHER_ERROR 256

/* Types */

typedef enum LauncherResult
{
	LAUNCHER_OK,
	LAUNCHER_FAILED,			// said in the error text
	LAUNCHER_SIGNED_OUT,		// the server no longer accepts the token (it ran out): sign in again
} LauncherResult;

// One build, for this system: the file of it that matches the platform
typedef struct LauncherBuild
{
	s32 build_id;
	s32 file_id;
	char version[48];
	char notes[1024];
	char file_name[160];
	u64 size;
} LauncherBuild;

typedef struct Launcher
{
	char server[256];
	char email[128];
	char token[1024];			// empty when nobody is signed in
	s32 announced_build;		// the newest build the person has been told about, so a notification comes once

	char data_dir[LAUNCHER_PATH];
	LauncherBuild builds[LAUNCHER_MAX_BUILDS];		// newest first
	u32 build_count;
} Launcher;

/* Functions */

// Finds the data folder and reads the settings. The server can be changed with PLAYTEST_SERVER (for testing).
bool launcher_init(Launcher* launcher);
bool launcher_save(const Launcher* launcher);

bool launcher_signed_in(const Launcher* launcher);
LauncherResult launcher_login(Launcher* launcher, const char* email, const char* password, char* error, size_t error_size);
void launcher_logout(Launcher* launcher);

// Asks the server for the builds that have a file for this system, and keeps them in launcher->builds
LauncherResult launcher_refresh(Launcher* launcher, char* error, size_t error_size);

bool launcher_is_installed(const Launcher* launcher, const LauncherBuild* build);
// Downloads and unpacks a build. `progress` gets what has arrived and what is expected, and can cancel.
LauncherResult launcher_install(Launcher* launcher, const LauncherBuild* build, HttpProgress progress, void* user,
	char* error, size_t error_size);
bool launcher_play(const Launcher* launcher, const LauncherBuild* build, char* error, size_t error_size);

// The newest build the person has not been told about and does not have yet, or NULL
const LauncherBuild* launcher_unannounced(const Launcher* launcher);

// The background check: asks now, and says so on the desktop when there is something new or the
// person has to sign in again. One round of it; launcher_watch is the loop around it.
void launcher_check_once(Launcher* launcher);
void launcher_watch(Launcher* launcher);

#endif // LAUNCHER_H
