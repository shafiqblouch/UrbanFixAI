// Only the two weights the app uses, so the other two are not bundled.
import { NotoNaskhArabic_400Regular } from "@expo-google-fonts/noto-naskh-arabic/400Regular";
import { NotoNaskhArabic_700Bold } from "@expo-google-fonts/noto-naskh-arabic/700Bold";
import { useFonts } from "expo-font";
import { StatusBar } from "expo-status-bar";
import { SafeAreaProvider } from "react-native-safe-area-context";
import { I18nProvider } from "./src/i18n";
import { Navigation } from "./src/navigation";
import { ServicesProvider } from "./src/services";
import { SessionProvider } from "./src/session";

export default function App() {
  // Urdu text needs a bundled font: low-end phones often have none. The app opens without waiting for it (English needs none).
  useFonts({ NotoNaskhArabic_400Regular, NotoNaskhArabic_700Bold });
  return (
    <SafeAreaProvider>
      <I18nProvider>
        <SessionProvider>
          <ServicesProvider>
            <StatusBar style="auto" />
            <Navigation />
          </ServicesProvider>
        </SessionProvider>
      </I18nProvider>
    </SafeAreaProvider>
  );
}
