const base64Pattern = /^[A-Za-z0-9+/]*={0,2}$/;
/** base64 的字节数由长度算出，所以读取大文件不必真的解码一遍；不是合法 base64 返回 -1。 */
export function base64Bytes(value: string): number {
    if (!base64Pattern.test(value) || value.length % 4 !== 0) return -1;
    const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0;
    return (value.length / 4) * 3 - padding;
}
export function bytesToBase64(bytes: Uint8Array): string {
    let binary = '';
    const chunk = 0x8000;
    for (let offset = 0; offset < bytes.length; offset += chunk) binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
    return btoa(binary);
}
export function base64ToBytes(value: string): Uint8Array<ArrayBuffer> {
    const binary = atob(value),
        bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return bytes;
}
