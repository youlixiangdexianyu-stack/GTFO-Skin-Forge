import { createTheme, type MantineColorsTuple } from '@mantine/core';

// Signal-flare orange (primary action, selection) and gunmetal greys.
const flare: MantineColorsTuple = ['#fff1e8', '#ffdfcc', '#ffbd96', '#ff9a5e', '#ff7a2f', '#ff6a14', '#f35f0a', '#d84f02', '#c04400', '#a83800'];
const dark: MantineColorsTuple = ['#e9e4d8', '#b9b4a8', '#9a958b', '#6c6963', '#2d3238', '#23272c', '#1a1d21', '#141619', '#101214', '#0b0c0e'];

export const theme = createTheme({
  primaryColor: 'flare',
  primaryShade: 4,
  colors: { flare, dark },
  black: '#141619',
  white: '#e9e4d8',
  fontFamily: "'Segoe UI Variable Text', 'Segoe UI', 'Microsoft YaHei UI', 'PingFang SC', sans-serif",
  fontFamilyMonospace: "'JetBrains Mono', 'Cascadia Mono', Consolas, monospace",
  headings: { fontFamily: "'Saira Stencil One', 'Segoe UI', 'Microsoft YaHei UI', sans-serif", fontWeight: '400' },
  defaultRadius: 'sm',
  fontSizes: { xs: '11.5px', sm: '13px', md: '14px', lg: '16px', xl: '20px' },
  cursorType: 'pointer',
  components: {
    NumberInput: { defaultProps: { size: 'xs', hideControls: false } },
    TextInput: { defaultProps: { size: 'xs' } },
    Textarea: { defaultProps: { size: 'xs' } },
    Select: { defaultProps: { size: 'xs', allowDeselect: false } },
    ColorInput: { defaultProps: { size: 'xs' } },
    Button: { defaultProps: { size: 'xs' } },
    SegmentedControl: { defaultProps: { size: 'xs' } },
    Tooltip: { defaultProps: { openDelay: 400, withArrow: true } },
  },
});
