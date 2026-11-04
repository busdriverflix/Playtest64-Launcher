/*
	The Windows side. The program asks for UTF-8 as its code page (res/launcher.manifest), so the "A"
	functions here take UTF-8 paths and a user name with an umlaut in it is no problem; only the
	notification, which the shell wants as wide text, converts by hand.
*/

#include "../platform.h"

#include <windows.h>
#include <shellapi.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define RUN_KEY "Software\\Microsoft\\Windows\\CurrentVersion\\Run"
#define RUN_VALUE "Playtest64Launcher"
#define NOTIFICATION_SHOWN_MS 10000			// how long the icon stays in the tray, which is what keeps the message up

/* Functions */

const char* platform_name(void)
{
	return "Windows";
}

bool platform_mkdir_p(const char* path)
{
	char copy[MAX_PATH * 2];

	if (snprintf(copy, sizeof(copy), "%s", path) >= (int)sizeof(copy))
		return false;

	for (char* c = copy; *c != '\0'; c++)
	{
		if (*c == '/')
			*c = '\\';
	}

	// a drive ("C:\") is not made, and the loop starts after the first character of the path
	for (char* c = copy + 3; *c != '\0'; c++)
	{
		if (*c != '\\')
			continue;

		*c = '\0';

		if (!CreateDirectoryA(copy, NULL) && GetLastError() != ERROR_ALREADY_EXISTS)
			return false;

		*c = '\\';
	}

	return CreateDirectoryA(copy, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
}

bool platform_data_dir(char* out, size_t size)
{
	const char* home_override = getenv("PLAYTEST_LAUNCHER_HOME");
	const char* local = getenv("LOCALAPPDATA");

	if (home_override != NULL && home_override[0] != '\0')
		snprintf(out, size, "%s", home_override);
	else if (local != NULL && local[0] != '\0')
		snprintf(out, size, "%s\\Playtest64Launcher", local);
	else
		return false;

	return platform_mkdir_p(out);
}

bool platform_self_path(char* out, size_t size)
{
	DWORD length = GetModuleFileNameA(NULL, out, (DWORD)size);

	return length > 0 && length < size;
}

bool platform_file_exists(const char* path)
{
	return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}

bool platform_make_executable(const char* path)
{
	(void)path;			// Windows runs a program by its name, not by a permission bit

	return true;
}

bool platform_replace_file(const char* from, const char* to)
{
	return MoveFileExA(from, to, MOVEFILE_REPLACE_EXISTING) != 0;
}

bool platform_launch(const char* program, const char* working_dir)
{
	char exe[MAX_PATH * 2], folder[MAX_PATH * 2], command[MAX_PATH * 2 + 4];

	snprintf(exe, sizeof(exe), "%s", program);
	snprintf(folder, sizeof(folder), "%s", working_dir);

	for (char* c = exe; *c != '\0'; c++)
	{
		if (*c == '/')
			*c = '\\';
	}

	for (char* c = folder; *c != '\0'; c++)
	{
		if (*c == '/')
			*c = '\\';
	}

	snprintf(command, sizeof(command), "\"%s\"", exe);

	STARTUPINFOA startup;
	PROCESS_INFORMATION info;

	memset(&startup, 0, sizeof(startup));
	startup.cb = sizeof(startup);

	if (!CreateProcessA(exe, command, NULL, NULL, FALSE, 0, NULL, folder, &startup, &info))
		return false;

	CloseHandle(info.hThread);
	CloseHandle(info.hProcess);

	return true;
}

void platform_notify(const char* title, const char* text)
{
	// a window the notification belongs to; it is never shown
	HWND window = CreateWindowExA(0, "STATIC", "", 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, GetModuleHandleA(NULL), NULL);

	if (window == NULL)
		return;

	NOTIFYICONDATAW icon;

	memset(&icon, 0, sizeof(icon));
	icon.cbSize = sizeof(icon);
	icon.hWnd = window;
	icon.uID = 1;
	icon.uFlags = NIF_ICON | NIF_TIP | NIF_INFO;
	icon.hIcon = LoadIconA(NULL, IDI_INFORMATION);
	icon.dwInfoFlags = NIIF_INFO;

	MultiByteToWideChar(CP_UTF8, 0, "Playtest64", -1, icon.szTip, ARRAYSIZE(icon.szTip));
	MultiByteToWideChar(CP_UTF8, 0, title, -1, icon.szInfoTitle, ARRAYSIZE(icon.szInfoTitle));
	MultiByteToWideChar(CP_UTF8, 0, text, -1, icon.szInfo, ARRAYSIZE(icon.szInfo));

	if (Shell_NotifyIconW(NIM_ADD, &icon))
	{
		Sleep(NOTIFICATION_SHOWN_MS);
		Shell_NotifyIconW(NIM_DELETE, &icon);
	}

	DestroyWindow(window);
}

bool platform_autostart_get(void)
{
	HKEY key;
	bool present = false;

	if (RegOpenKeyExA(HKEY_CURRENT_USER, RUN_KEY, 0, KEY_READ, &key) == ERROR_SUCCESS)
	{
		present = RegQueryValueExA(key, RUN_VALUE, NULL, NULL, NULL, NULL) == ERROR_SUCCESS;
		RegCloseKey(key);
	}

	return present;
}

bool platform_autostart_set(const bool on)
{
	HKEY key;

	if (RegCreateKeyExA(HKEY_CURRENT_USER, RUN_KEY, 0, NULL, 0, KEY_SET_VALUE, NULL, &key, NULL) != ERROR_SUCCESS)
		return false;

	bool done;

	if (on)
	{
		char self[MAX_PATH], command[MAX_PATH + 16];

		if (!platform_self_path(self, sizeof(self)))
		{
			RegCloseKey(key);
			return false;
		}

		snprintf(command, sizeof(command), "\"%s\" --watch", self);
		done = RegSetValueExA(key, RUN_VALUE, 0, REG_SZ, (const BYTE*)command, (DWORD)strlen(command) + 1) == ERROR_SUCCESS;
	}
	else
	{
		LONG result = RegDeleteValueA(key, RUN_VALUE);
		done = result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND;
	}

	RegCloseKey(key);

	return done;
}

void platform_sleep_seconds(const u32 seconds)
{
	Sleep(seconds * 1000u);
}
