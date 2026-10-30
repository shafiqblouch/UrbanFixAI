import { createBottomTabNavigator } from "@react-navigation/bottom-tabs";
import { DarkTheme, DefaultTheme, NavigationContainer, createNavigationContainerRef } from "@react-navigation/native";
import { createNativeStackNavigator } from "@react-navigation/native-stack";
import { useCallback, useEffect, useRef } from "react";
import { useColorScheme } from "react-native";
import { useT } from "./i18n";
import type { Key } from "./i18n/strings";
import { isMobileRole, tabsByRole, type MobileRole, type TabId } from "./roles";
import { ComplaintDetailScreen, ComplaintsScreen, HomeScreen, NewComplaintScreen, ReceiptScreen, ReceiptsScreen } from "./screens/resident";
import { CostsScreen, JobDetailScreen, JobsScreen, MaterialsScreen, TodayScreen } from "./screens/technician";
import { LoginScreen } from "./screens/LoginScreen";
import { SettingsScreen } from "./screens/SettingsScreen";
import { NoticesScreen, useUnreadNotices } from "./screens/notices";
import { onNotificationTap } from "./platform/push";
import { targetFor } from "./pushLink";
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
  Materials: { jobId: string };
};

const navigationRef = createNavigationContainerRef<RootStackParams>();
const Stack = createNativeStackNavigator<RootStackParams>();
const Tab = createBottomTabNavigator();

const screens: Record<TabId, React.ComponentType> = {
  home: HomeScreen, complaints: ComplaintsScreen, new: NewComplaintScreen, notices: NoticesScreen, today: TodayScreen, jobs: JobsScreen, settings: SettingsScreen,
};
const glyphs: Record<TabId, string> = { home: "⌂", complaints: "☰", new: "＋", notices: "✉", today: "☀", jobs: "🔧", settings: "⚙" };

/** The bottom tabs for the current role (RA-01, TA-01). The upload backlog shows as a badge on settings. */
function Tabs() {
  const t = useT();
  const { view } = useSession();
  const { waiting } = useServices();
  const p = usePalette();
  const unread = useUnreadNotices();
  const tabs = tabsByRole[view!.role as keyof typeof tabsByRole];
  return (
    <Tab.Navigator screenOptions={{ tabBarActiveTintColor: p.primary, tabBarStyle: { minHeight: 64, backgroundColor: p.surfaceRaised, borderTopColor: p.borderSoft }, tabBarLabelStyle: { fontSize: 13, fontWeight: "600" }, tabBarInactiveTintColor: p.textMuted, headerStyle: { backgroundColor: p.primary }, headerTintColor: p.onPrimary, headerTitleStyle: { fontWeight: "700" } }}>
      {tabs.map((id) => (
        <Tab.Screen key={id} name={id} component={screens[id]}
          options={{
            title: t(`tab.${id}` as Key),
            tabBarIcon: ({ color }) => <Text style={{ color, fontSize: 22 }}>{glyphs[id]}</Text>,
            tabBarBadge: id === "settings" && waiting > 0 ? waiting : id === "notices" && unread > 0 ? unread : undefined,
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
  const role = view?.role;
  // Tapping a notification opens its screen. The stack only exists once the person is signed in, so a tap that arrives earlier waits.
  const pending = useRef<string | undefined>(undefined);
  const open = useCallback((link: string | undefined) => {
    const target = isMobileRole(role ?? "") ? targetFor(link, role as MobileRole) : undefined;
    if (!target || !navigationRef.isReady()) { pending.current = link; return; }
    pending.current = undefined;
    (navigationRef.navigate as (name: string, params: object) => void)(target.name, target.params);
  }, [role]);
  useEffect(() => onNotificationTap(open), [open]);
  if (loading) return <Screen><Loading /></Screen>;

  const signedIn = !!me?.organizationId;
  return (
    <NavigationContainer ref={navigationRef} onReady={() => { if (pending.current) open(pending.current); }} theme={scheme === "dark" ? DarkTheme : DefaultTheme}>
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
            <Stack.Screen name="Materials" component={MaterialsScreen} options={{ title: t("mat.title") }} />
          </>
        )}
      </Stack.Navigator>
    </NavigationContainer>
  );
}

export { Badge };
