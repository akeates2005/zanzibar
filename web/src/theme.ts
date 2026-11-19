import { createTheme, type MantineColorsTuple } from '@mantine/core';

/** Indigo-violet brand scale, light to dark; shade 6 is the primary in light mode, 8 in dark (`primaryShade`). */
const brand: MantineColorsTuple = [
  '#eef0ff',
  '#dcdeff',
  '#b6b9fb',
  '#8d92f6',
  '#6b70f2',
  '#5559f0',
  '#4a4cef',
  '#3b3dd5',
  '#3335bf',
  '#272ba9',
];

const FONT = "'Inter Variable', Inter, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";

/** The client's look: one place for colour, type, radius and the defaults the components share. */
export const theme = createTheme({
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 8 },
  colors: { brand },
  fontFamily: FONT,
  headings: { fontFamily: FONT, fontWeight: '650' },
  defaultRadius: 'md',
  cursorType: 'pointer',
  shadows: {
    xs: '0 1px 2px rgba(16, 24, 40, 0.05)',
    sm: '0 1px 3px rgba(16, 24, 40, 0.08), 0 1px 2px rgba(16, 24, 40, 0.04)',
    md: '0 4px 12px rgba(16, 24, 40, 0.08)',
  },
  components: {
    Paper: { defaultProps: { radius: 'lg', shadow: 'xs' } },
    Card: { defaultProps: { radius: 'lg', shadow: 'xs' } },
    Badge: { defaultProps: { radius: 'sm' } },
    Button: { defaultProps: { radius: 'md' } },
    Tabs: { defaultProps: { radius: 'md' } },
  },
});
