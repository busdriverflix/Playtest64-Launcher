#include "ui.h"
#include "platform.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <termios.h>
#include <unistd.h>

/* Functions */

static bool read_line(const char* prompt, char* out, size_t size, const bool hidden)
{
	printf("%s", prompt);
	fflush(stdout);

	struct termios saved;
	bool changed = false;

	// a password is not shown as it is typed; when the input is a pipe (the tests) it is read as it is
	if (hidden && isatty(STDIN_FILENO) && tcgetattr(STDIN_FILENO, &saved) == 0)
	{
		struct termios quiet = saved;

		quiet.c_lflag &= ~(tcflag_t)ECHO;
		tcsetattr(STDIN_FILENO, TCSANOW, &quiet);
		changed = true;
	}

	bool got = fgets(out, (int)size, stdin) != NULL;

	if (changed)
	{
		tcsetattr(STDIN_FILENO, TCSANOW, &saved);
		printf("\n");
	}

	if (got)
		out[strcspn(out, "\r\n")] = '\0';

	return got;
}

static bool sign_in(Launcher* launcher)
{
	for (;;)
	{
		char email[128], password[256], error[LAUNCHER_ERROR];

		printf("\nAnmelden (dieselben Daten wie auf der Webseite). Leere E-Mail beendet.\n");

		if (!read_line("E-Mail: ", email, sizeof(email), false) || email[0] == '\0')
			return false;

		if (!read_line("Passwort: ", password, sizeof(password), true))
			return false;

		if (launcher_login(launcher, email, password, error, sizeof(error)) == LAUNCHER_OK)
			return true;

		printf("%s\n", error);
	}
}

static bool show_progress(u64 received, u64 total, void* user)
{
	(void)user;

	if (total > 0)
		printf("\r  %3u %%  (%llu von %llu MB)", (unsigned)(received * 100 / total),
			(unsigned long long)(received >> 20), (unsigned long long)(total >> 20));
	else
		printf("\r  %llu MB", (unsigned long long)(received >> 20));

	fflush(stdout);

	return true;
}

static void list_builds(const Launcher* launcher)
{
	printf("\nBuilds für %s:\n", platform_name());

	if (launcher->build_count == 0)
		printf("  (keine)\n");

	for (u32 i = 0; i < launcher->build_count; i++)
	{
		const LauncherBuild* build = &launcher->builds[i];

		printf("  [%u] %s%s  (%llu MB)\n", i + 1, build->version, launcher_is_installed(launcher, build) ? "  - installiert" : "",
			(unsigned long long)(build->size >> 20));

		if (build->notes[0] != '\0' && i == 0)
			printf("      %.200s\n", build->notes);
	}
}

// Refreshes the list; false when the person has to sign in again
static bool refresh(Launcher* launcher)
{
	char error[LAUNCHER_ERROR];
	LauncherResult result = launcher_refresh(launcher, error, sizeof(error));

	if (result != LAUNCHER_OK)
		printf("%s\n", error);

	if (result == LAUNCHER_OK && launcher->build_count > 0)
	{
		// everything in the list has now been seen, so the background check does not announce it again
		launcher->announced_build = launcher->builds[0].build_id;
		launcher_save(launcher);
	}

	return result != LAUNCHER_SIGNED_OUT;
}

s32 ui_run(Launcher* launcher)
{
	printf("Playtest64 Launcher %s\n", LAUNCHER_VERSION);

	if (!launcher_signed_in(launcher) && !sign_in(launcher))
		return 0;

	if (!refresh(launcher) && !(sign_in(launcher) && refresh(launcher)))
		return 0;

	for (;;)
	{
		char line[64];

		list_builds(launcher);
		printf("\nNummer: installieren oder spielen   r: neu laden   a: Update-Check beim Anmelden %s   o: abmelden   q: beenden\n",
			platform_autostart_get() ? "ausschalten" : "einschalten");

		if (!read_line("> ", line, sizeof(line), false) || strcmp(line, "q") == 0)
			return 0;

		if (strcmp(line, "r") == 0)
		{
			if (!refresh(launcher) && !(sign_in(launcher) && refresh(launcher)))
				return 0;
		}
		else if (strcmp(line, "a") == 0)
		{
			if (!platform_autostart_set(!platform_autostart_get()))
				printf("Das ließ sich nicht umstellen.\n");
		}
		else if (strcmp(line, "o") == 0)
		{
			launcher_logout(launcher);
			printf("Abgemeldet.\n");
			return 0;
		}
		else
		{
			u32 number = (u32)atoi(line);

			if (number == 0 || number > launcher->build_count)
			{
				printf("Das ist keine Nummer aus der Liste.\n");
				continue;
			}

			const LauncherBuild* build = &launcher->builds[number - 1];
			char error[LAUNCHER_ERROR];

			if (!launcher_is_installed(launcher, build))
			{
				printf("Lade %s ...\n", build->version);

				LauncherResult result = launcher_install(launcher, build, show_progress, NULL, error, sizeof(error));

				printf("\n");

				if (result != LAUNCHER_OK)
				{
					printf("%s\n", error);

					if (result == LAUNCHER_SIGNED_OUT && !(sign_in(launcher) && refresh(launcher)))
						return 0;

					continue;
				}

				printf("Installiert.\n");
			}

			if (launcher_play(launcher, build, error, sizeof(error)))
			{
				printf("Das Spiel startet.\n");
				return 0;
			}

			printf("%s\n", error);
		}
	}
}
