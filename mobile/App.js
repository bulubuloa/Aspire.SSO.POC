import React, { useState, useEffect } from 'react';
import {
  SafeAreaView, ScrollView, View, Text, TextInput, TouchableOpacity,
  StyleSheet, ActivityIndicator, StatusBar, KeyboardAvoidingView, Platform,
} from 'react-native';
import { FontAwesome6 } from '@expo/vector-icons';
import * as WebBrowser from 'expo-web-browser';
import { login, getRewards, redeem } from './src/api';
import { storage } from './src/storage';
import { notify, confirmSignOut } from './src/notify';
import RewardBrowser from './src/RewardBrowser';
import { T, shadow } from './src/theme';

// The client's own session, kept in the OS keystore (localStorage on web) so login survives a restart.
const SESSION_KEY = 'client_session';

// Where Aspire sends the customer back to. Must match the ReturnUrl Aspire registered for us.
const RETURN_URL = 'democlient://redeemed';

const NEWS = [
  { t: 'Roadside cover now includes EV battery boost', s: 'Benefits · 2h ago', icon: 'car-battery' },
  { t: 'New lounge partners added in Bangkok and Danang', s: 'Travel · 5h ago', icon: 'plane-departure' },
  { t: 'Teleconsult wait times down 40% this quarter', s: 'Health · 1d ago', icon: 'stethoscope' },
];

export default function App() {
  const [tab, setTab] = useState('dashboard');
  const [session, setSession] = useState(null);   // authenticated by the CLIENT, not Aspire
  const [rewards, setRewards] = useState([]);
  const [busy, setBusy] = useState(false);
  const [redeeming, setRedeeming] = useState(null); // reward id mid-handoff
  const [rewardUrl, setRewardUrl] = useState(null);  // web: in-page browser overlay
  const [mode, setMode] = useState('jwt');           // demo switch: jwt | saml
  const [scenario, setScenario] = useState('');      // demo switch: negative tests
  const [redeemed, setRedeemed] = useState([]);      // reward ids confirmed this session
  const [u, setU] = useState('');
  const [p, setP] = useState('');
  const [loginErr, setLoginErr] = useState(null);

  useEffect(() => {
    (async () => {
      const raw = await storage.get(SESSION_KEY);
      if (raw) { try { setSession(JSON.parse(raw)); } catch { await storage.remove(SESSION_KEY); } }
      try { setRewards(await getRewards()); } catch { /* offline — the list just stays empty */ }
    })();
  }, []);

  // Web: the framed Aspire page posts back when the customer taps Close.
  // Native has no equivalent — openAuthSessionAsync resolves on the deep link instead.
  useEffect(() => {
    if (Platform.OS !== 'web') return;
    const onMessage = (e) => {
      if (e.data?.type !== 'aspire:redeemed') return;
      setRewardUrl(null);
      setRedeemed((prev) => (prev.includes(e.data.reward) ? prev : [...prev, e.data.reward]));
    };
    globalThis.addEventListener('message', onMessage);
    return () => globalThis.removeEventListener('message', onMessage);
  }, []);

  async function doLogin() {
    setBusy(true); setLoginErr(null);
    try {
      const user = await login(u.trim(), p);
      await storage.set(SESSION_KEY, JSON.stringify(user));
      setSession(user);
      setU(''); setP('');
    } catch (e) {
      setLoginErr(e.message);
    } finally {
      setBusy(false);
    }
  }

  async function signOut() {
    await storage.remove(SESSION_KEY);
    setSession(null);
  }

  // Tap Redeem → client backend signs + hands off to Aspire in the background →
  // we only get a launch URL and open it. No Aspire login is ever shown.
  async function onRedeem(reward) {
    setRedeeming(reward.id);
    try {
      const { launchUrl } = await redeem(session.username, reward.id, mode, scenario);
      // Native: an in-app browser sheet. Web: an in-page overlay — a popup tab reads as
      // "we navigated you away", which is not what the native handoff looks like.
      if (Platform.OS === 'web') { setRewardUrl(launchUrl); return; }

      // openAuthSessionAsync (not openBrowserAsync) so the sheet auto-dismisses when Aspire
      // deep-links back to democlient://redeemed.
      const res = await WebBrowser.openAuthSessionAsync(launchUrl, RETURN_URL);
      if (res.type === 'success') {
        const id = new URL(res.url).searchParams.get('reward') ?? reward.id;
        setRedeemed((prev) => (prev.includes(id) ? prev : [...prev, id]));
      }
    } catch (e) {
      notify('Could not open reward', e.message);
    } finally {
      setRedeeming(null);
    }
  }

  function onPersonPress() {
    confirmSignOut(session.displayName, session.email, signOut);
  }

  const initials = session ? session.displayName.split(' ').map((w) => w[0]).join('') : null;
  const featured = rewards.filter((r) => r.featured);
  const others = rewards.filter((r) => !r.featured);

  // ---- The client app's OWN login page. Aspire is not involved at this point. ----
  if (!session) {
    return (
      <SafeAreaView style={s.screen}>
        <StatusBar barStyle="dark-content" backgroundColor={T.surface} />
        <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1 }}>
          <ScrollView contentContainerStyle={s.loginPage} keyboardShouldPersistTaps="handled">
            <View style={s.loginBrand}>
              <Text style={s.loginWordmark}>Demo Client</Text>
              <Text style={s.loginWordsub}>MEMBER APP</Text>
            </View>

            <View style={s.card}>
              <Text style={s.loginTitle}>Sign in</Text>
              <Text style={s.muted}>Your account with us. We never share your password.</Text>

              <Text style={s.label}>USERNAME</Text>
              <TextInput style={s.input} value={u} onChangeText={setU} autoCapitalize="none"
                autoCorrect={false} placeholder="jane / arjun / mai" placeholderTextColor={T.faint}
                returnKeyType="next" />
              <Text style={s.label}>PASSWORD</Text>
              <TextInput style={s.input} value={p} onChangeText={setP} secureTextEntry
                placeholder="demo" placeholderTextColor={T.faint}
                returnKeyType="go" onSubmitEditing={doLogin} />

              {loginErr && (
                <View style={s.errBox}>
                  <FontAwesome6 name="circle-exclamation" size={12} color={T.danger} solid />
                  <Text style={s.errTxt}>{loginErr}</Text>
                </View>
              )}

              <TouchableOpacity style={s.cta} onPress={doLogin} disabled={busy || !u || !p}>
                {busy ? <ActivityIndicator size="small" color={T.onBlue} />
                      : <><FontAwesome6 name="right-to-bracket" size={13} color={T.onBlue} solid />
                          <Text style={s.ctaTxt}>SIGN IN</Text></>}
              </TouchableOpacity>
            </View>

            <Text style={s.loginHint}>Demo users: jane · arjun · mai — password “demo”</Text>
          </ScrollView>
        </KeyboardAvoidingView>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={s.screen}>
      <StatusBar barStyle="dark-content" backgroundColor={T.surface} />

      <View style={s.header}>
        <View style={{ flex: 1 }}>
          <Text style={s.wordmark}>Demo Client</Text>
          <Text style={s.wordsub}>MEMBER APP</Text>
        </View>
        <TouchableOpacity style={[s.avatar, session && s.avatarOn]} onPress={onPersonPress}>
          {session
            ? <Text style={s.avatarTxt}>{initials}</Text>
            : <FontAwesome6 name="user" size={16} color={T.blue} solid />}
        </TouchableOpacity>
      </View>
      <View style={s.rule} />

      <ScrollView contentContainerStyle={s.body}>
        {tab === 'dashboard' ? (
          <>
            <View style={[s.card, { padding: 0, overflow: 'hidden' }]}>
              <View style={s.acctHead}>
                <View style={s.acctAvatar}><Text style={s.acctAvatarTxt}>{initials}</Text></View>
                <View style={{ flex: 1 }}>
                  <Text style={s.acctName}>{session.displayName}</Text>
                  <Text style={s.acctMail}>{session.email}</Text>
                </View>
              </View>

              <View style={s.acctBody}>
                <View style={s.kvRow}>
                  <View style={s.kv}><Text style={s.kvK}>MEMBER</Text><Text style={s.kvV}>{session.sub}</Text></View>
                  <View style={s.kv}><Text style={s.kvK}>PROGRAM</Text><Text style={s.kvV}>{session.program}</Text></View>
                </View>
                <View style={s.kvRow}>
                  <View style={s.kv}><Text style={s.kvK}>COUNTRY</Text><Text style={s.kvV}>{session.country}</Text></View>
                  <View style={s.kv}><Text style={s.kvK}>POINTS</Text><Text style={[s.kvV, { color: T.blue }]}>2,450</Text></View>
                </View>
              </View>
            </View>

            <View style={s.card}>
              <Text style={s.h2}>Weather</Text>
              <View style={s.wxRow}>
                <FontAwesome6 name="cloud-sun" size={40} color={T.warning} solid />
                <View style={{ flex: 1 }}>
                  <Text style={s.wxTemp}>31°</Text>
                  <Text style={s.muted}>Partly cloudy · Ho Chi Minh City</Text>
                </View>
              </View>
              <View style={s.wxDays}>
                {[['Mon', 'cloud-sun', '31°'], ['Tue', 'cloud-rain', '28°'], ['Wed', 'sun', '33°'], ['Thu', 'cloud-sun', '30°']].map(([d, i, t]) => (
                  <View key={d} style={s.wxDay}>
                    <Text style={s.muted}>{d}</Text>
                    <FontAwesome6 name={i} size={16} color={T.faint} solid style={{ marginVertical: 5 }} />
                    <Text style={s.wxDayT}>{t}</Text>
                  </View>
                ))}
              </View>
            </View>

            <View style={s.card}>
              <Text style={s.h2}>News</Text>
              {NEWS.map((n, i) => (
                <View key={n.t} style={[s.news, i === 0 && { borderTopWidth: 0 }]}>
                  <View style={s.newsIco}><FontAwesome6 name={n.icon} size={15} color={T.blue} solid /></View>
                  <View style={{ flex: 1 }}>
                    <Text style={s.newsT}>{n.t}</Text>
                    <Text style={s.muted}>{n.s}</Text>
                  </View>
                </View>
              ))}
            </View>
          </>
        ) : (
          <>
            {/* Demo switches — a real client app would ship neither of these. */}
            <View style={[s.card, s.demoBar]}>
              <View style={s.demoRow}>
                <Text style={s.demoLbl}>SSO MODE</Text>
                <View style={s.seg}>
                  {[['jwt', 'JWT'], ['saml', 'SAML 2.0']].map(([v, l]) => (
                    <TouchableOpacity key={v} style={[s.segBtn, mode === v && s.segOn]} onPress={() => setMode(v)}>
                      <Text style={[s.segTxt, mode === v && s.segTxtOn]}>{l}</Text>
                    </TouchableOpacity>
                  ))}
                </View>
              </View>
              <View style={[s.demoRow, { marginTop: 10 }]}>
                <Text style={s.demoLbl}>SCENARIO</Text>
                <View style={s.chips}>
                  {[['', 'Happy'], ['expired', 'Expired'], ['wrong-aud', 'Wrong aud'], ['tampered', 'Tampered'], ['bad-secret', 'Bad secret']].map(([v, l]) => (
                    <TouchableOpacity key={v || 'ok'} style={[s.chip, scenario === v && s.chipOn]} onPress={() => setScenario(v)}>
                      <Text style={[s.chipTxt, scenario === v && s.chipTxtOn]}>{l}</Text>
                    </TouchableOpacity>
                  ))}
                </View>
              </View>
              <Text style={s.demoHint}>
                {mode === 'jwt'
                  ? 'JWT — signed server-side and POSTed to Aspire back-channel. The token never reaches this app.'
                  : 'SAML — the browser carries the signed assertion to Aspire. You will see it redirect.'}
                {scenario ? '  ·  Redeem should be REJECTED.' : ''}
              </Text>
            </View>

            {/* Featured — large cards */}
            {featured.map((o) => (
              <View key={o.id} style={[s.card, { padding: 0, overflow: 'hidden' }]}>
                <View style={s.bigHead}>
                  <FontAwesome6 name={o.icon} size={52} color={T.blue} solid />
                  {!!o.tag && <View style={s.ribbon}><Text style={s.ribbonTxt}>{o.tag}</Text></View>}
                </View>
                <View style={s.bigBody}>
                  <Text style={s.bigTitle}>{o.title}</Text>
                  <Text style={[s.muted, { marginTop: 4 }]}>{o.detail}</Text>
                  <View style={s.bigFoot}>
                    <View>
                      <Text style={s.bigPts}>{o.points.toLocaleString()}</Text>
                      <Text style={s.bigPtsLbl}>POINTS</Text>
                    </View>
                    <TouchableOpacity style={[s.bigBtn, redeemed.includes(o.id) && s.bigBtnDone]}
                      onPress={() => onRedeem(o)} disabled={redeeming === o.id || redeemed.includes(o.id)}>
                      {redeeming === o.id
                        ? <ActivityIndicator size="small" color={T.onBlue} />
                        : redeemed.includes(o.id)
                          ? <><FontAwesome6 name="check" size={12} color={T.success} solid />
                              <Text style={[s.bigBtnTxt, { color: T.success }]}>REDEEMED</Text></>
                          : <><Text style={s.bigBtnTxt}>REDEEM</Text>
                              <FontAwesome6 name="arrow-right" size={12} color={T.onBlue} solid /></>}
                    </TouchableOpacity>
                  </View>
                </View>
              </View>
            ))}

            {/* Everything else — compact rows */}
            <View style={s.card}>
              <Text style={s.h2}>More offers</Text>
              {others.map((o, i) => (
                <View key={o.id} style={[s.offer, i === 0 && { borderTopWidth: 0 }]}>
                  <View style={s.offerIco}><FontAwesome6 name={o.icon} size={17} color={T.blue} solid /></View>
                  <View style={{ flex: 1 }}>
                    <Text style={s.offerT}>{o.title}</Text>
                    <Text style={s.muted}>{o.points.toLocaleString()} pts · {o.detail}</Text>
                  </View>
                  <TouchableOpacity style={[s.redeem, redeemed.includes(o.id) && s.redeemDone]}
                    onPress={() => onRedeem(o)} disabled={redeeming === o.id || redeemed.includes(o.id)}>
                    {redeeming === o.id
                      ? <ActivityIndicator size="small" color="#fff" />
                      : <Text style={[s.redeemTxt, redeemed.includes(o.id) && s.redeemDoneTxt]}>
                          {redeemed.includes(o.id) ? '✓ REDEEMED' : 'REDEEM'}
                        </Text>}
                  </TouchableOpacity>
                </View>
              ))}
              {!rewards.length && <Text style={s.muted}>Loading rewards…</Text>}
            </View>
          </>
        )}
      </ScrollView>

      <View style={s.tabs}>
        {[['dashboard', 'house', 'Dashboard'], ['reward', 'gift', 'Reward']].map(([k, i, l]) => (
          <TouchableOpacity key={k} style={s.tab} onPress={() => setTab(k)}>
            <FontAwesome6 name={i} size={18} color={tab === k ? T.blue : T.faint} solid />
            <Text style={[s.tabTxt, tab === k && s.tabTxtOn]}>{l}</Text>
          </TouchableOpacity>
        ))}
      </View>

      <RewardBrowser url={rewardUrl} onClose={() => setRewardUrl(null)} />
    </SafeAreaView>
  );
}

const s = StyleSheet.create({
  screen: { flex: 1, backgroundColor: T.page },
  header: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 18, paddingVertical: 12, backgroundColor: T.surface },
  wordmark: { color: T.ink, fontSize: 19, fontWeight: '800', letterSpacing: -0.3 },
  wordsub: { color: T.blue, fontSize: 8, fontWeight: '700', letterSpacing: 2.5, marginTop: 2 },
  rule: { height: 2, backgroundColor: T.blue },
  avatar: { width: 40, height: 40, borderRadius: 20, backgroundColor: T.surface, borderWidth: 1.5, borderColor: T.blue, alignItems: 'center', justifyContent: 'center' },
  avatarOn: { backgroundColor: T.blue, borderColor: T.blue },
  avatarTxt: { fontSize: 14, color: T.onBlue, fontWeight: '700' },
  body: { padding: 16, paddingBottom: 24 },
  card: { backgroundColor: T.surface, borderRadius: T.radius, padding: 16, marginBottom: 14, ...shadow },
  h2: { color: T.ink, fontSize: 15, fontWeight: '700', marginBottom: 12 },
  muted: { color: T.muted, fontSize: 13 },
  wxRow: { flexDirection: 'row', alignItems: 'center', gap: 14 },
  wxTemp: { color: T.ink, fontSize: 32, fontWeight: '700' },
  wxDays: { flexDirection: 'row', marginTop: 14, borderTopWidth: 1, borderTopColor: T.border, paddingTop: 12 },
  wxDay: { flex: 1, alignItems: 'center' },
  wxDayT: { color: T.ink, fontSize: 13, fontWeight: '700' },
  news: { flexDirection: 'row', gap: 12, alignItems: 'center', paddingVertical: 11, borderTopWidth: 1, borderTopColor: T.border },
  newsIco: { width: 34, height: 34, borderRadius: 17, backgroundColor: T.blueSoft, alignItems: 'center', justifyContent: 'center' },
  newsT: { color: T.ink, fontSize: 14, fontWeight: '600', marginBottom: 2 },
  acctHead: { flexDirection: 'row', alignItems: 'center', gap: 12, backgroundColor: T.blueSoft, padding: 16 },
  acctAvatar: { width: 52, height: 52, borderRadius: 26, backgroundColor: T.blue, alignItems: 'center', justifyContent: 'center' },
  acctAvatarTxt: { color: T.onBlue, fontSize: 18, fontWeight: '700' },
  acctName: { color: T.ink, fontSize: 19, fontWeight: '700' },
  acctMail: { color: '#475569', fontSize: 13, marginTop: 1 },
  acctBody: { padding: 16 },
  kvRow: { flexDirection: 'row', gap: 10, marginBottom: 10 },
  kv: { flex: 1, backgroundColor: T.subtle, borderRadius: T.radiusSm, padding: 10 },
  kvK: { color: T.muted, fontSize: 9, fontWeight: '700', letterSpacing: 0.5, marginBottom: 3 },
  kvV: { color: T.ink, fontSize: 14, fontWeight: '700' },
  // featured "big card"
  bigHead: { height: 132, backgroundColor: T.blueSoft, alignItems: 'center', justifyContent: 'center' },
  ribbon: { position: 'absolute', top: 12, right: 12, backgroundColor: T.blue, borderRadius: T.pill, paddingHorizontal: 10, paddingVertical: 4 },
  ribbonTxt: { color: T.onBlue, fontSize: 9, fontWeight: '800', letterSpacing: 0.6 },
  bigBody: { padding: 16 },
  bigTitle: { color: T.ink, fontSize: 19, fontWeight: '700' },
  bigFoot: { flexDirection: 'row', alignItems: 'flex-end', justifyContent: 'space-between', marginTop: 14 },
  bigPts: { color: T.blue, fontSize: 24, fontWeight: '800' },
  bigPtsLbl: { color: T.faint, fontSize: 9, fontWeight: '700', letterSpacing: 0.8 },
  bigBtn: { flexDirection: 'row', alignItems: 'center', gap: 8, backgroundColor: T.blue, borderRadius: T.pill, paddingHorizontal: 20, paddingVertical: 12, minWidth: 118, justifyContent: 'center' },
  bigBtnTxt: { color: T.onBlue, fontWeight: '800', fontSize: 12, letterSpacing: 0.5 },

  // demo switch
  demoBar: { backgroundColor: '#f8fafc', borderWidth: 1, borderColor: T.border },
  demoRow: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  demoLbl: { color: T.muted, fontSize: 9, fontWeight: '800', letterSpacing: 0.8, width: 62 },
  demoHint: { color: T.faint, fontSize: 10, marginTop: 10, lineHeight: 14 },
  seg: { flexDirection: 'row', backgroundColor: '#e2e8f0', borderRadius: 8, padding: 3, flex: 1 },
  segBtn: { flex: 1, paddingVertical: 6, borderRadius: 6, alignItems: 'center' },
  segOn: { backgroundColor: T.blue },
  segTxt: { color: T.muted, fontWeight: '700', fontSize: 11 },
  segTxtOn: { color: T.onBlue },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 5, flex: 1 },
  chip: { backgroundColor: '#e2e8f0', borderRadius: 999, paddingHorizontal: 8, paddingVertical: 4 },
  chipOn: { backgroundColor: T.danger },
  chipTxt: { color: T.muted, fontSize: 10, fontWeight: '600' },
  chipTxtOn: { color: '#fff' },

  offer: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 11, borderTopWidth: 1, borderTopColor: T.border },
  offerIco: { width: 40, height: 40, borderRadius: T.radiusSm, backgroundColor: T.blueSoft, alignItems: 'center', justifyContent: 'center' },
  offerT: { color: T.ink, fontSize: 14, fontWeight: '600' },
  redeem: { backgroundColor: T.ink, borderRadius: T.pill, paddingHorizontal: 14, paddingVertical: 8, minWidth: 74, alignItems: 'center' },
  redeemTxt: { color: '#fff', fontWeight: '700', fontSize: 10, letterSpacing: 0.5 },
  redeemDone: { backgroundColor: '#d1fae5' },
  redeemDoneTxt: { color: T.success },
  bigBtnDone: { backgroundColor: '#d1fae5' },
  cta: { flexDirection: 'row', gap: 8, backgroundColor: T.blue, borderRadius: T.pill, paddingVertical: 14, alignItems: 'center', justifyContent: 'center', marginTop: 14 },
  ctaTxt: { color: T.onBlue, fontWeight: '700', fontSize: 13, letterSpacing: 0.5 },
  tabs: { flexDirection: 'row', borderTopWidth: 1, borderTopColor: T.border, backgroundColor: T.surface, paddingBottom: 6 },
  tab: { flex: 1, alignItems: 'center', paddingVertical: 10 },
  tabTxt: { color: T.faint, fontSize: 11, marginTop: 4, fontWeight: '600' },
  tabTxtOn: { color: T.blue, fontWeight: '700' },
  // login page
  loginPage: { flexGrow: 1, justifyContent: 'center', padding: 22 },
  loginBrand: { alignItems: 'center', marginBottom: 26 },
  loginWordmark: { color: T.ink, fontSize: 30, fontWeight: '800', letterSpacing: -0.5 },
  loginWordsub: { color: T.blue, fontSize: 10, fontWeight: '700', letterSpacing: 4, marginTop: 4 },
  loginTitle: { color: T.ink, fontSize: 24, fontWeight: '700', marginBottom: 4 },
  loginHint: { color: T.faint, fontSize: 12, textAlign: 'center', marginTop: 18 },
  label: { color: T.muted, fontSize: 10, fontWeight: '700', letterSpacing: 0.6, marginTop: 16, marginBottom: 6 },
  input: { backgroundColor: T.surface, borderWidth: 1, borderColor: T.border, borderRadius: T.radiusSm, padding: 13, fontSize: 16, color: T.ink },
  errBox: { flexDirection: 'row', alignItems: 'center', gap: 7, backgroundColor: '#fee2e2', borderRadius: T.radiusSm, padding: 10, marginTop: 14 },
  errTxt: { color: '#991b1b', fontSize: 12, flex: 1 },
});
