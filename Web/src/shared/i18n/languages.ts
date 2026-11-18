import catalog from '../../../locales/languages.json' with {type: 'json'};
import type {TranslationDictionary} from '../../../locales/types.ts';
import {localeNamespaces} from './namespace-routes.ts';
import {readLocale} from './locale-source.ts';

export interface LanguageDefinition {
    code: string;
    label: string;
    // BCP 47 tag for <html lang> and Intl formatting.
    htmlLang: string;
    // The text language an author's own text resource uses for this interface language (forge/text-resource.ts TextLanguage);
    // null when the game has no matching column: author text then follows the catalog's first language.
    textLanguage: string | null;
    // A complete language carries every key (the key check enforces it). Any other language may be partial: a key it lacks reads in the default language.
    complete: boolean;
    // Each namespace is an independent dynamic entry. Omitting the namespace
    // is reserved for callers that explicitly need a complete language.
    load: (namespace?: string) => Promise<TranslationDictionary>;
}

async function loadLanguage(code: string, namespace?: string): Promise<TranslationDictionary> {
    const selected = namespace === undefined ? localeNamespaces : [namespace];
    for (const name of selected) if (!localeNamespaces.includes(name)) throw Error('Unknown locale namespace: ' + name);
    const messages: Record<string, string> = {};
    await Promise.all(
        selected.map(async name => {
            const entries = await readLocale(code, name);
            for (const [key, value] of Object.entries(entries)) messages[name + '.' + key] = value;
        })
    );
    return messages;
}

// One catalog for the application and self-contained error-page builder: Web/locales/languages.json.
// Adding a language means adding Web/locales/<code>/ and one row there; no code changes.
export const languages: readonly LanguageDefinition[] = catalog.languages.map(entry => ({
    ...entry,
    complete: entry.complete === true,
    load: (namespace?: string) => loadLanguage(entry.code, namespace)
}));
export const defaultLanguage: string = catalog.default;
