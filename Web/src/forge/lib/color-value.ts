/** The fourth native channel is either opacity or an independent HDR intensity. */
export type ColorChannel = 'alpha' | 'intensity';
const clamp = (value: number) => Math.min(1, Math.max(0, value));
export const toHex = (color: readonly number[]) =>
    '#' +
    color
        .slice(0, 3)
        .map(channel =>
            Math.round(clamp(channel) * 255)
                .toString(16)
                .padStart(2, '0')
        )
        .join('');
/** sRGB 编码值 ↔ 线性值（0..1）。 */
export const srgbToLinear = (value: number) => (value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
export const linearToSrgb = (value: number) => (value <= 0.0031308 ? value * 12.92 : 1.055 * value ** (1 / 2.4) - 0.055);
export const fromHex = (hex: string): [number, number, number] =>
    [1, 3, 5].map(at => Math.round((parseInt(hex.slice(at, at + 2), 16) / 255) * 1000) / 1000) as [number, number, number];

/**
 * 一个浮点颜色读成「普通颜色 × 亮度」：有亮度通道时亮度就是第四分量（RGB 已乘过它）；没有时分量超过 1 的部分就是亮度。
 * 不透明度只在第四分量是不透明度时有。
 */
export function colorLook(value: readonly number[], channel?: ColorChannel): {base: [number, number, number]; brightness: number; opacity?: number} {
    const rgb = [value[0] ?? 0, value[1] ?? 0, value[2] ?? 0] as [number, number, number];
    const brightness = channel === 'intensity' ? (value[3] ?? 1) : Math.max(1, ...rgb),
        divide = brightness > 1e-6 ? brightness : 1;
    return {
        base: rgb.map(part => part / divide) as [number, number, number],
        brightness,
        ...(channel !== 'intensity' && value.length > 3 ? {opacity: value[3]!} : {})
    };
}
/** 普通颜色 × 亮度写回模组读的原值，第四分量照原来的写法。 */
export function colorValue(base: readonly number[], brightness: number, channel: ColorChannel | undefined, value: readonly number[]): number[] {
    const rgb = base.slice(0, 3).map(part => part * brightness);
    return channel === 'intensity' ? [...rgb, brightness] : value.length > 3 ? [...rgb, value[3]!] : rgb;
}
