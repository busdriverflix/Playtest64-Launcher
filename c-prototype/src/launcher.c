#include "launcher.h"
#include "json.h"
#include "platform.h"
#include "zip.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Defines */

#define CONFIG_FILE "launcher.cfg"
#define INSTALLED_MARKER "installed.txt"
#define WATCH_INTERVAL_SECONDS (6u * 60u * 60u)		// the background check looks every six hours, and once at the start

/* Functions */

static void say(char* error, size_t size, const char* text)
{
	if (error != NULL && size > 0)
		snprintf(error, size, "%s", text);
}

static void copy_text(char* out, size_t size, const char* text)
{
	snprintf(out, size, "%s", text != NULL ? text : "");
}

/* Settings */

bool launcher_init(Launcher* launcher)
{
	memset(launcher, 0, sizeof(*launcher));

	const char* server = getenv("PLAYTEST_SERVER");

	copy_text(launcher->server, sizeof(launcher->server), server != NULL && server[0] != '\0' ? server : LAUNCHER_DEFAULT_SERVER);

	if (!platform_data_dir(launcher->data_dir, sizeof(launcher->data_dir)))
		return false;

	char path[LAUNCHER_PATH + 64];
	snprintf(path, sizeof(path), "%s/" CONFIG_FILE, launcher->data_dir);

	FILE* file = fopen(path, "r");

	if (file == NULL)
		return true;				// the first start: nothing is known yet

	char line[1300];

	while (fgets(line, sizeof(line), file) != NULL)
	{
		line[strcspn(line, "\r\n")] = '\0';

		char* equals = strchr(line, '=');

		if (equals == NULL)
			continue;

		*equals = '\0';
		const char* value = equals + 1;

		if (strcmp(line, "email") == 0)
			copy_text(launcher->email, sizeof(launcher->email), value);
		else if (strcmp(line, "token") == 0)
			copy_text(launcher->token, sizeof(launcher->token), value);
		else if (strcmp(line, "announced_build") == 0)
			launcher->announced_build = atoi(value);
	}

	fclose(file);

	return true;
}

bool launcher_save(const Launcher* launcher)
{
	char path[LAUNCHER_PATH + 64];
	snprintf(path, sizeof(path), "%s/" CONFIG_FILE, launcher->data_dir);

	FILE* file = fopen(path, "w");

	if (file == NULL)
		return false;

	fprintf(file, "email=%s\ntoken=%s\nannounced_build=%d\n", launcher->email, launcher->token, launcher->announced_build);

	return fclose(file) == 0;
}

bool launcher_signed_in(const Launcher* launcher)
{
	return launcher->token[0] != '\0';
}

/* The server */

static void url_for(const Launcher* launcher, const char* path, char* out, size_t size)
{
	// an address that does not fit is left empty, which the request then refuses
	int length = snprintf(out, size, "%s%s", launcher->server, path);

	if (length < 0 || (size_t)length >= size)
		out[0] = '\0';
}

LauncherResult launcher_login(Launcher* launcher, const char* email, const char* password, char* error, size_t error_size)
{
	char quoted_email[256], quoted_password[512], body[1024];

	if (!json_quote(email, quoted_email, sizeof(quoted_email)) || !json_quote(password, quoted_password, sizeof(quoted_password)))
	{
		say(error, error_size, "E-Mail oder Passwort sind zu lang.");
		return LAUNCHER_FAILED;
	}

	// "launcher" asks for a token that lasts long enough that nobody signs in every two weeks
	snprintf(body, sizeof(body), "{\"email\":%s,\"password\":%s,\"launcher\":true}", quoted_email, quoted_password);

	char url[LAUNCHER_PATH];
	url_for(launcher, "/api/auth/login", url, sizeof(url));

	HttpRequest request = { .method = "POST", .url = url, .json_body = body };
	HttpResponse response;

	if (!http_perform(&request, &response, error, error_size))
		return LAUNCHER_FAILED;

	JsonValue* json = json_parse(response.body);
	LauncherResult result = LAUNCHER_FAILED;

	if (response.status == 200 && json_flag(json_member(json, "success"), false) && json_text(json_member(json, "token"), "")[0] != '\0')
	{
		copy_text(launcher->token, sizeof(launcher->token), json_text(json_member(json, "token"), ""));
		copy_text(launcher->email, sizeof(launcher->email), email);
		launcher_save(launcher);
		result = LAUNCHER_OK;
	}
	else
	{
		// the server's own words are the best explanation (wrong password, locked for a while)
		say(error, error_size, json_text(json_member(json, "message"), "Die Anmeldung ist fehlgeschlagen."));
	}

	json_free(json);
	http_response_free(&response);

	return result;
}

void launcher_logout(Launcher* launcher)
{
	launcher->token[0] = '\0';
	launcher->build_count = 0;
	launcher_save(launcher);
}

LauncherResult launcher_refresh(Launcher* launcher, char* error, size_t error_size)
{
	char url[LAUNCHER_PATH];
	url_for(launcher, "/api/builds", url, sizeof(url));

	HttpRequest request = { .method = "GET", .url = url, .bearer = launcher->token };
	HttpResponse response;

	if (!http_perform(&request, &response, error, error_size))
		return LAUNCHER_FAILED;

	if (response.status == 401)
	{
		http_response_free(&response);
		launcher->token[0] = '\0';
		launcher_save(launcher);
		say(error, error_size, "Die Anmeldung ist abgelaufen. Bitte melde dich neu an.");
		return LAUNCHER_SIGNED_OUT;
	}

	JsonValue* json = json_parse(response.body);

	if (response.status != 200 || json == NULL || json->type != JSON_ARRAY)
	{
		say(error, error_size, "Die Liste der Builds konnte nicht geladen werden.");
		json_free(json);
		http_response_free(&response);
		return LAUNCHER_FAILED;
	}

	launcher->build_count = 0;

	for (u32 i = 0; i < json_count(json) && launcher->build_count < LAUNCHER_MAX_BUILDS; i++)
	{
		const JsonValue* entry = json_item(json, i);
		const JsonValue* files = json_member(entry, "files");

		for (u32 f = 0; f < json_count(files); f++)
		{
			const JsonValue* file = json_item(files, f);

			if (strcmp(json_text(json_member(file, "platform"), ""), platform_name()) != 0 ||
				!json_flag(json_member(file, "available"), false))
				continue;

			LauncherBuild* build = &launcher->builds[launcher->build_count++];

			memset(build, 0, sizeof(*build));
			build->build_id = (s32)json_number(json_member(entry, "id"), 0);
			build->file_id = (s32)json_number(json_member(file, "id"), 0);
			build->size = (u64)json_number(json_member(file, "fileSize"), 0);
			copy_text(build->version, sizeof(build->version), json_text(json_member(entry, "version"), "?"));
			copy_text(build->notes, sizeof(build->notes), json_text(json_member(entry, "notes"), ""));
			copy_text(build->file_name, sizeof(build->file_name), json_text(json_member(file, "fileName"), "build.zip"));
			break;
		}
	}

	json_free(json);
	http_response_free(&response);

	return LAUNCHER_OK;
}

/* Builds on disk */

// A version as a folder name: nothing that could climb out of the builds folder
static void folder_name(const LauncherBuild* build, char* out, size_t size)
{
	size_t used = 0;

	for (const char* c = build->version; *c != '\0' && used + 1 < size; c++)
	{
		bool plain = (*c >= '0' && *c <= '9') || (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || *c == '.' || *c == '-' || *c == '_';
		out[used++] = plain ? *c : '_';
	}

	out[used] = '\0';

	if (used == 0)
		snprintf(out, size, "build-%d", build->build_id);
}

static void build_dir(const Launcher* launcher, const LauncherBuild* build, char* out, size_t size)
{
	char name[64];
	folder_name(build, name, sizeof(name));
	snprintf(out, size, "%s/builds/%s", launcher->data_dir, name);
}

// What the marker file says: the folder the zip unpacked its game into
static bool installed_top(const Launcher* launcher, const LauncherBuild* build, char* top, size_t size)
{
	char path[LAUNCHER_PATH + 64];
	build_dir(launcher, build, path, sizeof(path));
	strncat(path, "/" INSTALLED_MARKER, sizeof(path) - strlen(path) - 1);

	FILE* file = fopen(path, "r");

	if (file == NULL)
		return false;

	top[0] = '\0';

	if (fgets(top, (int)size, file) != NULL)
		top[strcspn(top, "\r\n")] = '\0';

	fclose(file);

	return true;
}

bool launcher_is_installed(const Launcher* launcher, const LauncherBuild* build)
{
	char top[128];

	return installed_top(launcher, build, top, sizeof(top));
}

LauncherResult launcher_install(Launcher* launcher, const LauncherBuild* build, HttpProgress progress, void* user,
	char* error, size_t error_size)
{
	char downloads[LAUNCHER_PATH + 16], zip_path[LAUNCHER_PATH + 192], part_path[LAUNCHER_PATH + 200];

	snprintf(downloads, sizeof(downloads), "%s/downloads", launcher->data_dir);
	platform_mkdir_p(downloads);

	// the name is the server's; only its last part is used, so it can never point somewhere else
	const char* base = build->file_name;

	for (const char* c = build->file_name; *c != '\0'; c++)
	{
		if (*c == '/' || *c == '\\')
			base = c + 1;
	}

	snprintf(zip_path, sizeof(zip_path), "%s/%s", downloads, base[0] != '\0' ? base : "build.zip");
	snprintf(part_path, sizeof(part_path), "%s.part", zip_path);

	char path[LAUNCHER_PATH];
	snprintf(path, sizeof(path), "/api/builds/files/%d/download", build->file_id);

	char url[LAUNCHER_PATH + 64];
	url_for(launcher, path, url, sizeof(url));

	HttpRequest request = { .method = "GET", .url = url, .bearer = launcher->token, .save_path = part_path,
		.progress = progress, .user = user };
	HttpResponse response;

	if (!http_perform(&request, &response, error, error_size))
	{
		remove(part_path);
		return LAUNCHER_FAILED;
	}

	s32 status = response.status;
	JsonValue* json = status == 200 ? NULL : json_parse(response.body);

	if (status != 200)
	{
		LauncherResult result = LAUNCHER_FAILED;

		if (status == 401)
		{
			launcher->token[0] = '\0';
			launcher_save(launcher);
			say(error, error_size, "Die Anmeldung ist abgelaufen. Bitte melde dich neu an.");
			result = LAUNCHER_SIGNED_OUT;
		}
		else
		{
			say(error, error_size, json_text(json_member(json, "message"), "Der Download ist fehlgeschlagen."));
		}

		json_free(json);
		http_response_free(&response);
		remove(part_path);

		return result;
	}

	http_response_free(&response);

	if (!platform_replace_file(part_path, zip_path))
	{
		say(error, error_size, "Die heruntergeladene Datei lässt sich nicht ablegen.");
		return LAUNCHER_FAILED;
	}

	char dir[LAUNCHER_PATH], top[128];
	build_dir(launcher, build, dir, sizeof(dir));

	if (!platform_mkdir_p(dir) || !zip_extract(zip_path, dir, top, sizeof(top), error, error_size))
	{
		remove(zip_path);
		return LAUNCHER_FAILED;
	}

	// the marker comes last: a build that was cut off halfway is not an installed one
	char marker[LAUNCHER_PATH + 64];
	snprintf(marker, sizeof(marker), "%s/" INSTALLED_MARKER, dir);

	FILE* file = fopen(marker, "w");

	if (file == NULL)
	{
		say(error, error_size, "Die Installation lässt sich nicht abschließen.");
		return LAUNCHER_FAILED;
	}

	fprintf(file, "%s\n", top);
	fclose(file);
	remove(zip_path);

	return LAUNCHER_OK;
}

bool launcher_play(const Launcher* launcher, const LauncherBuild* build, char* error, size_t error_size)
{
	char top[128];

	if (!installed_top(launcher, build, top, sizeof(top)))
	{
		say(error, error_size, "Dieser Build ist nicht installiert.");
		return false;
	}

	char dir[LAUNCHER_PATH], folder[LAUNCHER_PATH + 160], program[LAUNCHER_PATH + 192];
	build_dir(launcher, build, dir, sizeof(dir));
	snprintf(folder, sizeof(folder), "%s%s%s", dir, top[0] != '\0' ? "/" : "", top);

	// "game" is the Linux starter (which checks the libraries), "game.exe" the Windows executable
	snprintf(program, sizeof(program), "%s/%s", folder, strcmp(platform_name(), "Windows") == 0 ? "game.exe" : "game");

	if (!platform_file_exists(program))
	{
		say(error, error_size, "Im Build fehlt das Programm \"game\".");
		return false;
	}

	if (!platform_launch(program, folder))
	{
		say(error, error_size, "Das Spiel lässt sich nicht starten.");
		return false;
	}

	return true;
}

/* Noticing something new */

const LauncherBuild* launcher_unannounced(const Launcher* launcher)
{
	if (launcher->build_count == 0)
		return NULL;

	const LauncherBuild* newest = &launcher->builds[0];

	if (newest->build_id == launcher->announced_build || launcher_is_installed(launcher, newest))
		return NULL;

	return newest;
}

void launcher_check_once(Launcher* launcher)
{
	if (!launcher_signed_in(launcher))
		return;

	char error[LAUNCHER_ERROR];
	LauncherResult result = launcher_refresh(launcher, error, sizeof(error));

	if (result == LAUNCHER_SIGNED_OUT)
	{
		platform_notify("Playtest64", "Bitte melde dich im Launcher neu an, damit nach Updates gesucht werden kann.");
		return;
	}

	const LauncherBuild* fresh = launcher_unannounced(launcher);

	if (result != LAUNCHER_OK || fresh == NULL)
		return;

	char text[256];
	snprintf(text, sizeof(text), "Version %s ist da. Öffne den Launcher, um sie zu installieren.", fresh->version);
	platform_notify("Neuer Playtest-Build", text);

	launcher->announced_build = fresh->build_id;
	launcher_save(launcher);
}

void launcher_watch(Launcher* launcher)
{
	for (;;)
	{
		// the settings may have been changed by the launcher window in the meantime (signed in, signed out)
		Launcher fresh;

		if (launcher_init(&fresh))
		{
			launcher_check_once(&fresh);
			*launcher = fresh;
		}

		platform_sleep_seconds(WATCH_INTERVAL_SECONDS);
	}
}
