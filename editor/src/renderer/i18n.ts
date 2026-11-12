// UI language: Chinese and English. Strings live in locales/zh.json and locales/en.json under the same keys; the types
// below make a key missing from either file fail `pnpm typecheck`. The choice is kept in the app's settings; the first
// start follows the system: Chinese when any of the system's preferred languages is Chinese, otherwise English.
import i18next from 'i18next';
import { initReactI18next, useTranslation } from 'react-i18next';
import en from './locales/en.json';
import zh from './locales/zh.json';

export type Lang = 'zh' | 'en';

// Both files must carry exactly the same keys.
const sameKeysEn: Record<keyof typeof zh, string> = en;
const sameKeysZh: Record<keyof typeof en, string> = zh;
void sameKeysEn;
void sameKeysZh;

declare module 'i18next' {
  interface CustomTypeOptions {
    resources: { translation: typeof zh };
    keySeparator: false;
  }
}

export const systemLang = (): Lang => (navigator.languages.some((l) => l.toLowerCase().startsWith('zh')) ? 'zh' : 'en');

void i18next.use(initReactI18next).init({
  resources: { zh: { translation: zh }, en: { translation: en } },
  lng: systemLang(),
  fallbackLng: 'zh',
  keySeparator: false,
  interpolation: { escapeValue: false },
  returnNull: false,
});

export const t = i18next.t.bind(i18next);
export const setLang = (lang: Lang) => void i18next.changeLanguage(lang);
export const lang = (): Lang => (i18next.language === 'en' ? 'en' : 'zh');
/** Re-renders the caller when the language changes. */
export const useLang = (): Lang => (useTranslation().i18n.language === 'en' ? 'en' : 'zh');

/** Catalog names follow the language: English uses `name`, Chinese `nameZh` with `name` as the fallback. */
export const localName = (item: { name: string; nameZh?: string | null }) => (lang() === 'zh' && item.nameZh) || item.name;
export const localCategory = (item: { category: string; categoryZh?: string | null }) =>
  lang() === 'zh' ? item.categoryZh || item.category : item.categoryZh ? fromZh(item.categoryZh) : item.category;

// Words the backend sends only in Chinese (part labels, weapon types) are listed under `name.*`; English looks them up by
// the Chinese text and keeps it when it is not listed.
const byZh = new Map(Object.entries(zh).flatMap(([k, v]) => (k.startsWith('name.') ? [[v, k as keyof typeof zh]] : [])));
export function fromZh(text: string): string {
  const k = lang() === 'en' && byZh.get(text);
  return k ? t(k) : text;
}

/** A part's display name: the backend's Chinese label, or its English name. */
export const partLabel = (p: { id: string; label: string }) => (lang() === 'zh' ? p.label : byZh.has(p.label) ? fromZh(p.label) : p.id.replace(/_/g, ' '));
