import { Platform } from 'react-native';
import * as SecureStore from 'expo-secure-store';

// SecureStore is native-only — on web it throws
// "_ExpoSecureStore.default.setValueWithKeyAsync is not a function".
// Web is a demo/testing convenience, so fall back to localStorage there.
//
// localStorage is NOT secure storage: any script on the page can read it. Native builds get the
// real Keychain/Keystore; the web target is for demos only, never a shipping client.
const web = Platform.OS === 'web';

export const storage = {
  async get(key) {
    if (web) return globalThis.localStorage?.getItem(key) ?? null;
    return SecureStore.getItemAsync(key);
  },
  async set(key, value) {
    if (web) return void globalThis.localStorage?.setItem(key, value);
    return SecureStore.setItemAsync(key, value);
  },
  async remove(key) {
    if (web) return void globalThis.localStorage?.removeItem(key);
    return SecureStore.deleteItemAsync(key);
  },
};
