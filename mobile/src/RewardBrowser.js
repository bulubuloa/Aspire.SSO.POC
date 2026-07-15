import React from 'react';
import { View, Text, TouchableOpacity, StyleSheet, Modal, Platform } from 'react-native';
import { FontAwesome6 } from '@expo/vector-icons';
import { T } from './theme';

// Web stand-in for the native in-app browser.
//
// On native, WebBrowser.openBrowserAsync opens a sheet OVER the app. On web that would be a popup
// tab, which reads as "we navigated you away" rather than "a browser opened inside the app" — so
// here we frame the Aspire page in an overlay instead.
//
// Demo-only. Real Aspire would almost certainly send X-Frame-Options/CSP and refuse to be framed,
// and cross-site cookies would need SameSite=None; Secure. It works here because :8082 and :6001
// are both `localhost` — cookies ignore the port, so the session cookie is same-site.
export default function RewardBrowser({ url, onClose }) {
  if (Platform.OS !== 'web' || !url) return null;

  const host = (() => { try { return new URL(url).host; } catch { return url; } })();

  return (
    <Modal visible transparent animationType="slide" onRequestClose={onClose}>
      <View style={s.backdrop}>
        <View style={s.sheet}>
          {/* chrome that mimics an in-app browser bar */}
          <View style={s.bar}>
            <TouchableOpacity onPress={onClose} style={s.close} accessibilityLabel="Close">
              <FontAwesome6 name="xmark" size={16} color={T.ink} solid />
            </TouchableOpacity>
            <View style={s.urlBox}>
              <FontAwesome6 name="lock" size={10} color={T.muted} solid />
              <Text style={s.url} numberOfLines={1}>{host}</Text>
            </View>
          </View>

          {/* React renders to the DOM on web, so a raw iframe is fine here */}
          <View style={s.frameWrap}>
            {React.createElement('iframe', {
              src: url,
              style: { border: 'none', width: '100%', height: '100%' },
              title: 'Aspire reward',
            })}
          </View>
        </View>
      </View>
    </Modal>
  );
}

const s = StyleSheet.create({
  backdrop: { flex: 1, backgroundColor: 'rgba(15,23,42,.5)', justifyContent: 'flex-end' },
  sheet: { height: '92%', backgroundColor: T.surface, borderTopLeftRadius: 14, borderTopRightRadius: 14, overflow: 'hidden' },
  bar: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 12, paddingVertical: 10,
         backgroundColor: '#f1f5f9', borderBottomWidth: 1, borderBottomColor: T.border },
  close: { width: 30, height: 30, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  urlBox: { flex: 1, flexDirection: 'row', alignItems: 'center', gap: 6, backgroundColor: T.surface,
            borderRadius: 8, paddingHorizontal: 10, paddingVertical: 6 },
  url: { color: T.muted, fontSize: 12 },
  frameWrap: { flex: 1, backgroundColor: '#fff' },
});
