import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { DarkTheme, DefaultTheme, NavigationContainer } from "@react-navigation/native";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { useColorScheme } from "react-native";
import { useT } from "./i18n";
import type { Key } from "./i18n/strings";
import { isMobileRole, tabsByRole, type TabId } from "./roles";
import { ComplaintDetailScreen, ComplaintsScreen, HomeScreen, NewComplaintScreen, ReceiptScreen, ReceiptsScreen } from "./screens/resident";
import { CostsScreen, JobDetailScreen, JobsScreen, TodayScreen } from "./screens/technician";
import { LoginScreen } from "./screens/LoginScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { useServices } from "./services";
import { useSession } from "./session";
import { Badge, Empty, Loading, Screen, Button } from "./ui";
import { usePalette } from "./theme";
import { Text } from "react-native";

export type RootStackParams = {
  Login: undefined;
  Unsupported: undefined;
  Tabs: undefined;
  ComplaintDetail: { id: string };
  Receipt: { id: string };
  Receipts: undefined;
  JobDetail: { id: string };
  Costs: { jobId: string; mode: "estimate" | "actuals" };
};

const Stack = createNativeStackNavigator<RootStackParams>();
const Tab = createBottomTabNavigator();

const screens: Record<TabId, React.ComponentType> = {
  home: HomeScreen, complaints: ComplaintsScreen, new: NewComplaintScreen, today: TodayScreen, jobs: JobsScreen, settings: SettingsScreen,
};
const glyphs: Record<TabId, string> = { home: "⌂", complaints: "☰", new: "＋", today: "☀", jobs: "🔧", settings: "⚙" };

/** The bottom tabs for the current role (RA-01, TA-01). The upload backlog shows as a badge on settings. */
function Tabs() {
  const t = useT();
  const { view } = useSession();
  const { waiting } = useServices();
  const p = usePalette();
  const tabs = tabsByRole[view!.role as keyof typeof tabsByRole];
  return (
    <Tab.Navigator screenOptions={{ tabBarActiveTintColor: p.primary, tabBarStyle: { minHeight: 64 }, tabBarLabelStyle: { fontSize: 13 }, headerStyle: { backgroundColor: p.surface }, headerTintColor: p.text }}>
      {tabs.map((id) => (
        <Tab.Screen key={id} name={id} component={screens[id]}
          options={{
            title: t(`tab.${id}` as Key),
            tabBarIcon: ({ color }) => <Text style={{ color, fontSize: 22 }}>{glyphs[id]}</Text>,
            tabBarBadge: id === "settings" && waiting > 0 ? waiting : undefined,
          }} />
      ))}
    </Tab.Navigator>
  );
}

function Unsupported() {
  const t = useT();
  const { signOut } = useSession();
  return (
    <Screen>
      <Empty text={t("login.unsupported")} />
      <Button title={t("settings.signOut")} variant="secondary" onPress={() => void signOut()} />
    </Screen>
  );
}

export function Navigation() {
  const t = useT();
  const { me, view, loading } = useSession();
  const scheme = useColorScheme();
  if (loading) return <Screen><Loading /></Screen>;

  const signedIn = !!me?.organizationId;
  return (
    <NavigationContainer theme={scheme === "dark" ? DarkTheme : DefaultTheme}>
      <Stack.Navigator>
        {!signedIn ? (
          <Stack.Screen name="Login" component={LoginScreen} options={{ headerShown: false }} />
        ) : !view || !isMobileRole(view.role) ? (
          <Stack.Screen name="Unsupported" component={Unsupported} options={{ title: t("app.name") }} />
        ) : (
          <>
            <Stack.Screen name="Tabs" component={Tabs} options={{ headerShown: false }} />
            <Stack.Screen name="ComplaintDetail" component={ComplaintDetailScreen} options={{ title: t("tab.complaints") }} />
            <Stack.Screen name="Receipt" component={ReceiptScreen} options={{ title: t("receipt.title") }} />
            <Stack.Screen name="Receipts" component={ReceiptsScreen} options={{ title: t("receipt.list") }} />
            <Stack.Screen name="JobDetail" component={JobDetailScreen} options={{ title: t("tab.jobs") }} />
            <Stack.Screen name="Costs" component={CostsScreen} options={{ title: t("cost.title") }} />
          </>
        )}
      </Stack.Navigator>
    </NavigationContainer>
  );
}

export { Badge };
