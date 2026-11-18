// One language, one flat key → text map. A locale file must never carry more
// than one language: `site/locales/<code>/<domain>.ts` is the complete copy for
// that language only, so two languages can never drift inside one entry.
export type TranslationDictionary = Readonly<Record<string, string>>;
