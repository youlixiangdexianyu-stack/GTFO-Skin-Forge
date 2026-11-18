import {decode, encode} from 'fast-png';
import {requireImported} from './imported-model-format.ts';
import {linearToSrgb, srgbToLinear} from './lib/color-value.ts';

// 导入模型的表面贴图重新打包（合同 §8「玩家上传模型」）。游戏的装备着色器（Cell/Player/CustomGearShader，读自游戏的着色器属性表）只有
// 四张贴图：_MainTex 反照率、_MetallicGlossMap「Metalness (R), Smoothness (G)」、_BumpMap 法线、_EmissiveMap 自发光；没有遮挡贴图、
// 金属度/光滑度系数、法线强度。glTF 的金属粗糙贴图（G 粗糙度、B 金属度）因此在这里改成 R 金属度、G 光滑度（= 1 − 粗糙度），系数乘进去；
// 法线强度乘进法线贴图；环境光遮挡乘进反照率。输入都是转换器已归一成的 8 位、非隔行 PNG（颜色类型 2 或 6）。

/** 8 位 RGBA 像素。 */
export interface Pixels {
    width: number;
    height: number;
    data: Uint8Array;
}

export function decodePixels(png: Uint8Array): Pixels {
    const image = decode(png);
    requireImported(image.depth === 8 && image.data instanceof Uint8Array && (image.channels === 3 || image.channels === 4), 'image-decode');
    const count = image.width * image.height,
        data = new Uint8Array(count * 4);
    for (let at = 0; at < count; at++) {
        for (let channel = 0; channel < 3; channel++) data[at * 4 + channel] = image.data[at * image.channels + channel]!;
        data[at * 4 + 3] = image.channels === 4 ? image.data[at * 4 + 3]! : 255;
    }
    return {width: image.width, height: image.height, data};
}

export const encodePixels = (pixels: Pixels): Uint8Array => encode({width: pixels.width, height: pixels.height, data: pixels.data, channels: 4, depth: 8});

const unit = (value: number) => (value < 0 ? 0 : value > 1 ? 1 : value);
const toByte = (value: number) => Math.round(unit(value) * 255);
const srgbByteToLinear = Float32Array.from({length: 256}, (_, byte) => srgbToLinear(byte / 255));
const linearToSrgbByte = (linear: number) => toByte(linearToSrgb(unit(linear)));

/** `source` 在 (x, y) 处的像素起点，按相对位置取最近点：贴图之间尺寸不同也能对齐。 */
const nearest = (source: Pixels, x: number, y: number, width: number, height: number) =>
    (Math.min(source.height - 1, Math.floor((y * source.height) / height)) * source.width +
        Math.min(source.width - 1, Math.floor((x * source.width) / width))) *
    4;

/** glTF 金属粗糙贴图（G 粗糙度、B 金属度）→ 装备着色器的 R 金属度、G 光滑度；金属度系数与粗糙度系数先乘进去。 */
export function packMetalSmooth(source: Pixels, metallicFactor: number, roughnessFactor: number): Pixels {
    const data = new Uint8Array(source.data.length);
    for (let at = 0; at < data.length; at += 4) {
        data[at] = toByte((source.data[at + 2]! / 255) * metallicFactor);
        data[at + 1] = toByte(1 - (source.data[at + 1]! / 255) * roughnessFactor);
        data[at + 3] = 255;
    }
    return {width: source.width, height: source.height, data};
}

/** glTF：法线 = 归一化((贴图×2−1) × (scale, scale, 1))。 */
export function scaleNormals(source: Pixels, scale: number): Pixels {
    const data = Uint8Array.from(source.data);
    for (let at = 0; at < data.length; at += 4) {
        const x = ((source.data[at]! / 255) * 2 - 1) * scale,
            y = ((source.data[at + 1]! / 255) * 2 - 1) * scale,
            z = (source.data[at + 2]! / 255) * 2 - 1,
            length = Math.hypot(x, y, z) || 1;
        data[at] = toByte((x / length + 1) / 2);
        data[at + 1] = toByte((y / length + 1) / 2);
        data[at + 2] = toByte((z / length + 1) / 2);
    }
    return {width: source.width, height: source.height, data};
}

/**
 * 把环境光遮挡（R 通道，`strength` 为 glTF 的遮挡强度）乘进反照率。反照率可以是一张贴图（sRGB），也可以是一个线性底色（此时输出尺寸取遮挡贴图）；
 * 乘法在线性空间做，透明度原样保留。
 */
export function bakeOcclusion(albedo: Pixels | null, baseColor: readonly number[], occlusion: Pixels, strength: number): Pixels {
    const width = albedo?.width ?? occlusion.width,
        height = albedo?.height ?? occlusion.height,
        data = new Uint8Array(width * height * 4);
    for (let y = 0; y < height; y++)
        for (let x = 0; x < width; x++) {
            const at = (y * width + x) * 4,
                shade = 1 + strength * (occlusion.data[nearest(occlusion, x, y, width, height)]! / 255 - 1);
            for (let channel = 0; channel < 3; channel++)
                data[at + channel] = linearToSrgbByte((albedo ? srgbByteToLinear[albedo.data[at + channel]!]! : baseColor[channel]!) * shade);
            data[at + 3] = albedo ? albedo.data[at + 3]! : 255;
        }
    return {width, height, data};
}

/** glTF 的基础色 = 贴图 × baseColorFactor：颜色在线性空间相乘，透明度直接相乘。 */
export function tintPixels(source: Pixels, factor: readonly number[]): Pixels {
    const data = new Uint8Array(source.data.length);
    for (let at = 0; at < data.length; at += 4) {
        for (let channel = 0; channel < 3; channel++) data[at + channel] = linearToSrgbByte(srgbByteToLinear[source.data[at + channel]!]! * factor[channel]!);
        data[at + 3] = toByte((source.data[at + 3]! / 255) * factor[3]!);
    }
    return {width: source.width, height: source.height, data};
}
