/** The one backslash-to-slash conversion and the one relative-path rule; website, server and tools all use these, and they
 *  mirror `ToPosix`, `IsRelative` and `IsRelativeFile` in ForgeCore `PosixPath.cs` rule for rule. Shared cases:
 *  `Tests/Forge/fixtures/posix-path-cases.json`. */
export const toPosix = (value: string): string => value.replaceAll('\\', '/');

/** A `/`-separated path below some root: not empty, no backslash, and every segment non-empty and neither `.` nor `..`, so
 *  no leading, trailing or doubled separator and no step out of the root. Unity hierarchy and asset paths use this as is,
 *  since an object name may end in a space; a path that names a file goes through `isRelativeFile`. */
export const isRelative = (path: string): boolean =>
    path !== '' && !path.includes('\\') && path.split('/').every(segment => segment !== '' && segment !== '.' && segment !== '..');

// Below U+0020, and the characters Windows refuses in a name (`:` also rules out a drive, a drive-relative path and a stream).
const forbiddenFileCharacter = /[\u0000-\u001f:*?"<>|]/;
// CON, PRN, AUX, NUL, COM1-9 or LPT1-9 in any case, alone or before an extension.
const deviceName = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i;

/** A relative path that names one file on every system: `isRelative`, no control character below U+0020, none of
 *  `: * ? " < > |`, no segment ending in a space or dot (Windows drops them, so two spellings would name one file) and no
 *  reserved device name. */
export const isRelativeFile = (path: string): boolean =>
    isRelative(path) && !forbiddenFileCharacter.test(path) && path.split('/').every(segment => !/[ .]$/.test(segment) && !deviceName.test(segment));
