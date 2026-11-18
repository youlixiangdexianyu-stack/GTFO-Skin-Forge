/** The one compatibility rule of every Forge package (contract 1.4): only a change of the leading major breaks.
 *  A requirement is a version whose major and minor are the lowest it needs; it is met by an installed version with the
 *  same major and a minor at or above it. Patch numbers never matter. The mods apply the same rule in
 *  `Mods/ForgeEngine/Framework/Serialization/ForgeVersion.cs`. */
const exact = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

function parts(version: string): [number, number] | undefined {
    const match = exact.exec(version);
    return match ? [Number(match[1]), Number(match[2])] : undefined;
}

export function forgeVersionMeets(installed: string, required: string): boolean {
    const have = parts(installed),
        need = parts(required);
    return have !== undefined && need !== undefined && have[0] === need[0] && have[1] >= need[1];
}

/** The version every Forge-owned data format, protocol and manifest declares, as the string `1.0.0`. Readers take it from here and refuse any other value; the mods read it from `Mods/ForgeEngine/Framework/Serialization/FormatVersion.cs`. */
export const formatVersion = '1.0.0';
