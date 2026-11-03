#define _GNU_SOURCE

#include "../platform.h"

#include <errno.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

/* Defines */

#define AUTOSTART_FILE "playtest64-launcher.desktop"

/* Functions */

const char* platform_name(void)
{
	return "Linux";
}

bool platform_mkdir_p(const char* path)
{
	char copy[PATH_MAX];

	if (snprintf(copy, sizeof(copy), "%s", path) >= (int)sizeof(copy))
		return false;

	for (char* c = copy + 1; *c != '\0'; c++)
	{
		if (*c != '/')
			continue;

		*c = '\0';

		if (mkdir(copy, 0755) != 0 && errno != EEXIST)
			return false;

		*c = '/';
	}

	return mkdir(copy, 0755) == 0 || errno == EEXIST;
}

bool platform_data_dir(char* out, size_t size)
{
	const char* home_override = getenv("PLAYTEST_LAUNCHER_HOME");
	const char* data_home = getenv("XDG_DATA_HOME");
	const char* home = getenv("HOME");

	if (home_override != NULL && home_override[0] != '\0')
		snprintf(out, size, "%s", home_override);
	else if (data_home != NULL && data_home[0] == '/')
		snprintf(out, size, "%s/playtest64-launcher", data_home);
	else if (home != NULL && home[0] == '/')
		snprintf(out, size, "%s/.local/share/playtest64-launcher", home);
	else
		return false;

	return platform_mkdir_p(out);
}

bool platform_self_path(char* out, size_t size)
{
	ssize_t length = readlink("/proc/self/exe", out, size - 1);

	if (length <= 0)
		return false;

	out[length] = '\0';

	return true;
}

bool platform_file_exists(const char* path)
{
	struct stat info;

	return stat(path, &info) == 0;
}

bool platform_make_executable(const char* path)
{
	return chmod(path, 0755) == 0;
}

bool platform_replace_file(const char* from, const char* to)
{
	return rename(from, to) == 0;			// rename replaces on Linux
}

bool platform_launch(const char* program, const char* working_dir)
{
	pid_t child = fork();

	if (child < 0)
		return false;

	if (child == 0)
	{
		// on its own: closing the launcher must not close the game
		setsid();

		if (chdir(working_dir) != 0)
			_exit(127);

		execl(program, program, (char*)NULL);
		_exit(127);
	}

	// the first fork is only waited for in the sense that a program that cannot start is noticed:
	// give it a moment, and a child that has already died means it could not be run
	usleep(150 * 1000);

	int status;

	if (waitpid(child, &status, WNOHANG) == child && WIFEXITED(status) && WEXITSTATUS(status) == 127)
		return false;

	signal(SIGCHLD, SIG_IGN);

	return true;
}

void platform_notify(const char* title, const char* text)
{
	// PLAYTEST_LAUNCHER_NOTIFY=print: to the terminal only (the tests read it there)
	const char* mode = getenv("PLAYTEST_LAUNCHER_NOTIFY");

	if (mode != NULL && strcmp(mode, "print") == 0)
	{
		fprintf(stderr, "%s: %s\n", title, text);
		return;
	}

	pid_t child = fork();

	if (child == 0)
	{
		execlp("notify-send", "notify-send", "--app-name=Playtest64", title, text, (char*)NULL);
		_exit(127);
	}

	if (child > 0)
	{
		int status = 0;
		waitpid(child, &status, 0);

		if (WIFEXITED(status) && WEXITSTATUS(status) == 0)
			return;
	}

	// no desktop to tell: the terminal, or the journal when it runs in the background
	fprintf(stderr, "%s: %s\n", title, text);
}

static bool autostart_path(char* out, size_t size)
{
	const char* config_home = getenv("XDG_CONFIG_HOME");
	const char* home = getenv("HOME");
	char folder[PATH_MAX];

	if (config_home != NULL && config_home[0] == '/')
		snprintf(folder, sizeof(folder), "%s/autostart", config_home);
	else if (home != NULL && home[0] == '/')
		snprintf(folder, sizeof(folder), "%s/.config/autostart", home);
	else
		return false;

	if (!platform_mkdir_p(folder))
		return false;

	return snprintf(out, size, "%s/" AUTOSTART_FILE, folder) < (int)size;
}

bool platform_autostart_get(void)
{
	char path[PATH_MAX];

	return autostart_path(path, sizeof(path)) && platform_file_exists(path);
}

bool platform_autostart_set(const bool on)
{
	char path[PATH_MAX], self[PATH_MAX];

	if (!autostart_path(path, sizeof(path)))
		return false;

	if (!on)
		return remove(path) == 0 || errno == ENOENT;

	if (!platform_self_path(self, sizeof(self)))
		return false;

	FILE* file = fopen(path, "w");

	if (file == NULL)
		return false;

	// the check that runs while the person is logged in; "Exec" quotes the path in case it has a space
	fprintf(file, "[Desktop Entry]\nType=Application\nName=Playtest64 Launcher (Update-Check)\n"
		"Exec=\"%s\" --watch\nNoDisplay=true\nX-GNOME-Autostart-enabled=true\n", self);

	return fclose(file) == 0;
}

void platform_sleep_seconds(const u32 seconds)
{
	sleep(seconds);
}
