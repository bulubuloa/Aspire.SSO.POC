// "Demo Client" brand — the CLIENT's own app.
//
// Deliberately NOT the Aspire palette. This app belongs to the client; Aspire only owns the
// redeem/benefit web-view that opens after the handoff. The visible brand switch (blue app ->
// red Aspire page) is what makes the SSO handoff obvious in the demo.
// Aspire's tokens live in backend/OmnicasaSso.Demo/wwwroot/aspire-theme.css.
export const T = {
  // brand
  blue: '#2563eb',       // primary action
  blueDark: '#1d4ed8',   // pressed
  blueSoft: '#dbeafe',   // tinted surfaces / account header
  ink: '#0f172a',        // headings
  navy: '#1e293b',

  // surfaces
  page: '#f1f5f9',
  surface: '#ffffff',
  border: '#e2e8f0',
  subtle: '#f1f5f9',

  // text
  body: '#334155',
  muted: '#64748b',
  faint: '#94a3b8',
  onBlue: '#ffffff',

  // status
  success: '#059669',
  warning: '#f59e0b',
  danger: '#dc2626',

  // shape
  radius: 16,
  radiusSm: 10,
  pill: 999,
};

export const shadow = {
  shadowColor: '#0f172a',
  shadowOpacity: 0.07,
  shadowRadius: 12,
  shadowOffset: { width: 0, height: 3 },
  elevation: 2,
};
