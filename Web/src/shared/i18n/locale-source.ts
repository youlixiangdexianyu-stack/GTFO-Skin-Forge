/** One namespace's messages from its JSON source. Node, the tests and the dev server read the source file;
 *  the production build replaces this module (Tools/Web/locale-files-plugin.ts) with hashed JSON fetched on demand.
 *  A partial language has no file for a namespace it has not started: that reads as no messages, and the reader falls back to the default language. */
export async function readLocale(code: string, name: string): Promise<Record<string, string>> {
    try {
        return (await import(`../../../locales/${code}/${name}.json`)).default;
    } catch {
        return {};
    }
}
