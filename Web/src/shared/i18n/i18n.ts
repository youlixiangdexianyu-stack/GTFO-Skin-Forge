import {createInstance} from 'i18next';
import {createListeners} from '../../forge/lib/listeners.ts';
import {languages, defaultLanguage} from './languages.ts';
import {localeNamespaces, commonNamespaces, laterNamespacesForPage, namespacesForPage} from './namespace-routes.ts';
export {languages} from './languages.ts';
export type {LanguageDefinition} from './languages.ts';
// Built-in copy is loaded once per language from the language catalog, through
// the catalog's own loader. Pages and tests cannot inject a second dictionary
// that hides a missing or stale locale file: there is no registration API, and
// the only way a language's messages enter this store is readyLanguage resolving
// that language's declared loader.
//
// The messages live in one i18next instance: a language's namespaces become its
// resource bundles, and a key is one flat string — `<namespace>.<key>`, the exact
// spelling every caller writes — because the first segment names the namespace and
// the rest is the key inside it. Every read passes its language explicitly, so no
// call can be answered with the copy of a language the reader did not choose;
// `fallbackLng: false` states the same rule for i18next itself.
const instance = createInstance();
// Cache by language and namespace; concurrent routes share the same pending load.
const loaded = new Set<string>();
const loading = new Map<string, Promise<void>>();
const required = new Set(commonNamespaces);
// The catalog, not the loaded dictionaries, decides which languages exist: a
// language is supported from the first frame, long before its copy arrives.
const codes = new Set(languages.map(definition => definition.code));
const htmlLanguages = new Map(languages.map(language => [language.code, language.htmlLang]));
const listeners = createListeners<[string]>();
// The most recent setLanguage request, so a slow dictionary cannot overwrite a later choice.
let requested: string | undefined;
const storageKey = 'infini-gtfo-language';
const supported = (value: unknown): value is string => typeof value === 'string' && codes.has(value);
const resolvedDefault = supported(defaultLanguage) ? defaultLanguage : languages[0].code;
// A first visit follows the browser's own preference order, so a Chinese
// browser opens in Chinese instead of English. Only an exact catalog code or its
// primary subtag counts; an unsupported language keeps the catalog default, and a
// regional variant is never mapped onto a different script, so the store cannot
// select a language the reader did not ask for.
export const languageFromBrowser = (): string | null => {
    const entries = (globalThis as {navigator?: {languages?: readonly string[]; language?: string}}).navigator;
    const preferred = entries ? [...(entries.languages ?? []), entries.language] : [];
    for (const entry of preferred) {
        if (typeof entry !== 'string' || !entry) continue;
        const tag = entry.toLowerCase();
        if (codes.has(tag)) return tag;
        const primary = tag.split('-')[0];
        if (primary && primary !== tag && codes.has(primary)) return primary;
    }
    return null;
};
const resolvePreferred = (saved: unknown) => (supported(saved) ? saved : (languageFromBrowser() ?? resolvedDefault));
let language = resolvedDefault;
// Initialization is synchronous (no backend loads copy in this store), so a key
// asked for before any language arrives still reads the same as it did from an
// empty dictionary: the key itself. The separators only state how a key is spelled;
// the store resolves the namespace of a key itself, below.
instance.init({
    lng: language,
    // Only a partial language falls back, to the default language; a complete one answers every key itself (the key check enforces it).
    fallbackLng: (code: string) => (languages.find(entry => entry.code === code)?.complete || code === resolvedDefault ? [] : [resolvedDefault]),
    defaultNS: false,
    nsSeparator: '.',
    keySeparator: false,
    initAsync: false,
    resources: {},
    interpolation: {prefix: '{', suffix: '}', escapeValue: false}
});
const hasBrowserDocument = () => typeof globalThis.document?.documentElement !== 'undefined';
// One language's dictionary, split back into the namespaces its keys name: the
// catalog loader stays the only door to the copy, and i18next receives it under
// the namespace every key already carries.
function register(code: string, messages: Record<string, string>) {
    const bundles: Record<string, Record<string, string>> = {};
    for (const [key, value] of Object.entries(messages)) {
        const separator = key.indexOf('.');
        if (separator <= 0 || separator === key.length - 1) throw Error('Locale key has no namespace: ' + code + ' ' + key);
        (bundles[key.slice(0, separator)] ??= {})[key.slice(separator + 1)] = value;
    }
    // The bundles are built here and owned by the store, and every value is a string (keySeparator: false): a shallow,
    // overwriting merge without i18next's defensive JSON copy gives the same dictionary as a deep merge, without the copy.
    for (const [namespace, entries] of Object.entries(bundles))
        instance.addResourceBundle(code, namespace, entries, false, true, {silent: false, skipCopy: true});
}
// A caller without a namespace selection explicitly requests the full language
// (for example bilingual package generation and Node tests), never the app shell.
export function readyLanguage(locale: string = language, namespaces: readonly string[] = localeNamespaces): Promise<void> {
    const code = supported(locale) ? locale : resolvedDefault;
    const definition = languages.find(entry => entry.code === code);
    if (!definition) return Promise.reject(Error('Missing language definition: ' + code));
    for (const name of namespaces) if (!localeNamespaces.includes(name)) return Promise.reject(Error('Unknown locale namespace: ' + name));
    // A partial language is read together with the default language, which answers every key it lacks.
    return Promise.all([
        readyOwn(definition, namespaces),
        ...(needsFallback(code) ? [readyOwn(languages.find(entry => entry.code === resolvedDefault)!, namespaces)] : [])
    ]).then(() => {});
}
const needsFallback = (code: string) => code !== resolvedDefault && !languages.find(entry => entry.code === code)?.complete;
function readyOwn(definition: {code: string; load: (namespace?: string) => Promise<Record<string, string>>}, namespaces: readonly string[]): Promise<void> {
    const code = definition.code;
    return Promise.all(
        namespaces.map(name => {
            required.add(name);
            const key = code + '/' + name;
            if (loaded.has(key)) return;
            let pending = loading.get(key);
            if (!pending) {
                pending = Promise.resolve()
                    .then(() => definition.load(name))
                    .then(messages => {
                        register(code, messages);
                        loaded.add(key);
                        copyArrived();
                    })
                    .finally(() => loading.delete(key));
                loading.set(key, pending);
            }
            return pending;
        })
    ).then(() => {});
}
/** The page waits for its own copy; what only its dynamically loaded parts show follows right after, without holding it. */
export function readyLanguageForPage(pathname: string): Promise<void> {
    return readyLanguage(language, namespacesForPage(pathname)).then(() => {
        readyLanguage(language, laterNamespacesForPage(pathname)).catch(() => {
            /* a key of a namespace still missing asks for it again (t) */
        });
    });
}
// Copy that lands after a component rendered: useLanguage re-renders on it, so a key read before its namespace arrived
// does not stay on screen. One notification per task, however many namespaces land together.
const copyListeners = createListeners();
let copyRevision = 0,
    copyPending = false;
function copyArrived() {
    if (copyPending) return;
    copyPending = true;
    queueMicrotask(() => {
        copyPending = false;
        copyRevision++;
        copyListeners.emit();
    });
}
export const onCopyArrived = copyListeners.subscribe;
export const getCopyRevision = () => copyRevision;
async function readyRequiredLanguage(code: string): Promise<void> {
    // A route may add namespaces while a language switch is pending.
    do {
        await readyLanguage(code, [...required]);
    } while ([...required].some(name => !loaded.has(code + '/' + name) || (needsFallback(code) && !loaded.has(resolvedDefault + '/' + name))));
}
if (hasBrowserDocument()) {
    // 跟着别的标签页或返回页面换语言：语言包没取到时记下原因，页面保持原来的语言。
    const follow = (next: string) => {
        if (next !== language) setLanguage(next).catch((error: unknown) => console.error(error));
    };
    window.addEventListener('storage', event => {
        if (event.key === storageKey || event.key === null) follow(supported(event.newValue) ? event.newValue : resolvedDefault);
    });
    window.addEventListener('pageshow', () => {
        let saved;
        try {
            saved = localStorage.getItem(storageKey);
        } catch {
            /* 浏览器存储不可用：跟随浏览器语言 */
        }
        follow(resolvePreferred(saved));
    });
}
// The stored preference is read whenever a global is available, not only with a
// full document: workers and tests still start in the reader's own language, and
// a saved choice still wins over the browser preference.
{
    let saved;
    try {
        saved = localStorage.getItem(storageKey);
    } catch {
        /* 浏览器存储不可用：跟随浏览器语言 */
    }
    language = resolvePreferred(saved);
}
export const getLanguage = () => language;
/** Text a data file supplies per language code, read in the current language; a language the data lacks falls back to the catalog default, then to whatever the data has. */
export const localizedOr = (record: object | undefined, locale: string = language): string | undefined => record && localized(record, locale);
export const localized = (record: object, locale: string = language): string => {
    const values = record as Readonly<Record<string, string | undefined>>;
    return values[locale] ?? values[defaultLanguage] ?? Object.values(values).find(value => value !== undefined) ?? '';
};
// BCP 47 tag for Intl formatting and <html lang>, from the language catalog.
export const languageTag = (locale = language) => htmlLanguages.get(locale) ?? locale;
// One key is `<namespace>.<key>`: the first segment names the namespace — the file
// the text lives in — and the rest is the key inside it, so a key spells as many
// separators as it likes (`logic.port.field`). i18next's own `nsSeparator` split
// would read the second segment as a second namespace, so the store resolves the
// namespace here and hands i18next a key it must not split again.
function lookup(key: string): {namespace: string; key: string} | null {
    const separator = key.indexOf('.');
    return separator > 0 && separator < key.length - 1 ? {namespace: key.slice(0, separator), key: key.slice(separator + 1)} : null;
}
// What one language's dictionary holds for that exact key: no fallback language, no
// other namespace and no plural or context variant, exactly like reading the loaded
// copy directly.
export function hasMessage(key: string | null | undefined, locale: string = language): boolean {
    if (typeof key !== 'string') return false;
    const target = lookup(key);
    return !!target && instance.getResource(locale, target.namespace, target.key, {keySeparator: false}) !== undefined;
}
export function t(key: string | null | undefined, params: Record<string, unknown> = {}, locale: string = language): string {
    if (typeof key !== 'string') return '';
    const target = lookup(key);
    // A key that names no namespace cannot be in any dictionary, so it reads as
    // itself — which is also what a missing key returns.
    if (!target) return key;
    // A namespace no page set named for this page yet (a part loaded later): fetch it; the copy re-renders when it lands.
    if (
        hasBrowserDocument() &&
        !loaded.has(locale + '/' + target.namespace) &&
        !loading.has(locale + '/' + target.namespace) &&
        localeNamespaces.includes(target.namespace)
    )
        readyLanguage(locale, [target.namespace]).catch(() => {
            /* the key stays readable as itself */
        });
    // A parameter explicitly set to `undefined` is not a value a sentence can print;
    // leaving it out keeps the placeholder for the caller to fill, which is what a
    // missing parameter has always done here. `defaultValue` is the full key so a
    // missing translation still reads as the key the caller wrote, namespace included.
    const values: Record<string, unknown> = {defaultValue: key};
    for (const [name, value] of Object.entries(params)) if (value !== undefined) values[name] = value;
    values.lng = locale;
    values.ns = target.namespace;
    values.nsSeparator = false;
    return String(instance.t(target.key, values));
}
function writeText(element: HTMLElement, value: string) {
    const nodes = Array.from(element.childNodes).filter(node => node.nodeType === 3);
    if (nodes.length) {
        nodes[0].textContent = value;
        nodes.slice(1).forEach(node => node.remove?.());
    } else element.prepend(document.createTextNode(value));
}
export function localizeDOM(root: Document | HTMLElement = document) {
    const nodes = [...root.querySelectorAll<HTMLElement>('[data-i18n], [data-i18n-placeholder], [data-i18n-title], [data-i18n-aria-label]')];
    if (root instanceof HTMLElement && root.matches('[data-i18n], [data-i18n-placeholder], [data-i18n-title], [data-i18n-aria-label]')) nodes.unshift(root);
    for (const node of nodes) {
        if (hasMessage(node.dataset.i18n)) writeText(node, t(node.dataset.i18n));
        for (const attr of ['placeholder', 'title', 'aria-label']) {
            const key = node.getAttribute('data-i18n-' + attr);
            if (hasMessage(key)) node.setAttribute(attr, t(key));
        }
    }
}
export const onLanguageChange = listeners.subscribe;
// The entry points above always pass an explicit root, so the store works
// without a DOM and never writes a preference the reader did not choose.
export function setLanguage(value: unknown, root: Document | HTMLElement | null = hasBrowserDocument() ? document : null): Promise<void> {
    // A stale or hand-edited preference must not select a language outside the
    // catalog: it normalises to the resolved default, exactly like the stored
    // preference paths above.
    const selected = supported(value) ? value : resolvedDefault;
    requested = selected;
    // The language code is the snapshot every React subscriber reads, so it may only
    // change once that language's copy is readable: switching first and loading later
    // would notify with a code that never changes again when the dictionary lands,
    // leaving mounted components on raw keys. A later switch supersedes this one, and
    // a failed load keeps the current language and surfaces as a rejection.
    if ([...required].every(name => loaded.has(selected + '/' + name) && (!needsFallback(selected) || loaded.has(resolvedDefault + '/' + name)))) {
        commit(selected, root);
        return Promise.resolve();
    }
    return readyRequiredLanguage(selected).then(() => {
        if (requested === selected) commit(selected, root);
    });
}
function commit(selected: string, root: Document | HTMLElement | null) {
    language = selected;
    apply(root);
}
function apply(root: Document | HTMLElement | null) {
    if (root) {
        try {
            localStorage.setItem(storageKey, language);
        } catch {
            /* 浏览器存储不可用：语言只对本次访问有效 */
        }
        document.documentElement.lang = languageTag(language);
        localizeDOM(root);
    }
    listeners.emit(language);
}

if (hasBrowserDocument()) {
    const initialize = () => {
        document.documentElement.lang = languageTag(language);
        localizeDOM();
    };
    // The document's own copy is already localised by the inline script in the
    // static page, so this pass waits for the catalog rather than blanking it to
    // keys; a failed load still applies the language tag.
    const start = () => {
        void readyLanguage(language, commonNamespaces).then(initialize, initialize);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, {once: true});
    else queueMicrotask(start);
}
