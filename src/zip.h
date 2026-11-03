/*
	Unpacks the zip a build comes in. The two methods a zip made by `zip` or by Python uses (stored and
	deflated), no encryption, no zip64: a build is far under the 4 GB that would need it, and a zip that
	does need it is refused with a message rather than unpacked wrongly.

	A name that would land outside the folder it is unpacked into (an absolute path, a "..") is refused,
	which is the one thing about unpacking somebody else's archive that must not be left out.
*/

#ifndef ZIP_H
#define ZIP_H

#include "types.h"

// Unpacks everything into dest_dir. `top` receives the name of the first folder in the archive (the
// builds come as one folder of their own), "" when the first entry is not in one.
bool zip_extract(const char* zip_path, const char* dest_dir, char* top, size_t top_size, char* error, size_t error_size);

#endif // ZIP_H
