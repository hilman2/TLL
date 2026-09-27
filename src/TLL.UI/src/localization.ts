import * as l10n from "cs2/l10n";

interface Localization {
  translate(id: string, fallback?: string | null): string | null;
}

// The template's types (types/l10n.d.ts) declare useCachedLocalization, but
// the game's cs2/l10n module does not export it at runtime (1.6.2f1); calling
// it throws and takes the panel down. useLocalization is exported and is what
// other mods use, so it is looked up by name.
const useLocalization: () => Localization = (l10n as unknown as { useLocalization: () => Localization }).useLocalization;

/**
 * Returns a translator for the panel's texts. Keys are the ones in
 * src/TLL/Localization/*.json without the "TLL." prefix the C# side adds.
 * The English fallback is shown if a key is missing everywhere, e.g. while
 * the C# part of the mod is older than the panel.
 */
export function useTranslate() {
  const localization = useLocalization();
  return (key: string, fallback: string) => localization.translate("TLL." + key, fallback) ?? fallback;
}
