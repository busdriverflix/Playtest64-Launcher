/*
	The Playtest64 launcher: a small program with no libraries of its own that signs a tester in, lists
	the builds of the game, installs the one they pick and starts it, and (with --watch) looks now and
	then in the background whether a new build has appeared and says so on the desktop.

	  launcher                 the interface
	  launcher --watch         the background check, which never ends: for the system's autostart
	  launcher --check         one background check, then it stops: for a timer
	  launcher --extract Z D   unpacks a zip into a folder (a tool for testing the unpacker)

	Author: Felix Röling
*/

#include "launcher.h"
#include "platform.h"
#include "ui.h"
#include "zip.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Functions */

int main(int argc, char** argv)
{
	if (argc == 4 && strcmp(argv[1], "--extract") == 0)
	{
		char top[128], error[LAUNCHER_ERROR];

		if (!platform_mkdir_p(argv[3]) || !zip_extract(argv[2], argv[3], top, sizeof(top), error, sizeof(error)))
		{
			fprintf(stderr, "%s\n", error);
			return 1;
		}

		printf("%s\n", top);
		return 0;
	}

	Launcher* launcher = (Launcher*)calloc(1, sizeof(Launcher));

	if (launcher == NULL || !launcher_init(launcher))
	{
		fprintf(stderr, "Der Ordner für die Daten des Launchers lässt sich nicht anlegen.\n");
		return 1;
	}

	s32 code = 0;

	if (argc > 1 && strcmp(argv[1], "--watch") == 0)
		launcher_watch(launcher);
	else if (argc > 1 && strcmp(argv[1], "--check") == 0)
		launcher_check_once(launcher);
	else
		code = ui_run(launcher);

	free(launcher);

	return code;
}
