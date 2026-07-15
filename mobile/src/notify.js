import { Alert, Platform } from 'react-native';

// react-native-web has no Alert implementation — calls silently do nothing.
// Fall back to the browser's own dialogs so errors are still visible on web.
const web = Platform.OS === 'web';

export function notify(title, message) {
  if (web) return void globalThis.alert(`${title}\n\n${message}`);
  Alert.alert(title, message);
}

export function confirmSignOut(name, email, onConfirm) {
  if (web) {
    if (globalThis.confirm(`${name}\n${email}\n\nSign out?`)) onConfirm();
    return;
  }
  Alert.alert(name, email, [
    { text: 'Sign out', style: 'destructive', onPress: onConfirm },
    { text: 'Close', style: 'cancel' },
  ]);
}
