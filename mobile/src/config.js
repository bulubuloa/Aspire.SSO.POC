import { Platform } from 'react-native';

// The app talks ONLY to the client's own backend (Client.Demo).
// It never contacts Aspire directly — the SSO handoff happens server-to-server, and the only
// Aspire URL the app ever sees is the one-time launchUrl it opens in a web-view.

// Hosted builds set this at build time (Expo inlines EXPO_PUBLIC_* into the bundle).
//   EXPO_PUBLIC_BACKEND_URL=https://client.yourdomain.com
const hosted = process.env.EXPO_PUBLIC_BACKEND_URL;

// --- local development ---
// A phone/emulator cannot reach "localhost" — that address means the device itself.
//  - Android emulator: 10.0.2.2 is a special alias to the host machine's loopback.
//  - iOS simulator / web: share the host network, so localhost works.
//  - Physical device: must use your computer's LAN IP (both on the same Wi-Fi).
//
// For a physical device, set LAN_IP below (find it with: ipconfig getifaddr en0).
const LAN_IP = null; // e.g. '192.168.1.20'
const PORT = 5001;
const localHost = LAN_IP ?? (Platform.OS === 'android' ? '10.0.2.2' : 'localhost');

export const BACKEND_URL = hosted || `http://${localHost}:${PORT}`;
