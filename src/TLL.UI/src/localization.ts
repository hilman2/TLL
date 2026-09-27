import { useCachedLocalization } from "cs2/l10n";

/**
 * Returns a translator for the panel's texts. Keys are the ones in
 * src/TLL/Localization/*.json without the "TLL." prefix the C# side adds.
 * The English fallback is shown if a key is missing everywhere, e.g. while
 * the C# part of the mod is older than the panel.
 */
export function useTranslate() {
  const localization = useCachedLocalization();
  return (key: string, fallback: string) => localization.translate("TLL." + key, fallback) ?? fallback;
}
