/** Re-encodes an image the browser can decode as PNG, pixel for pixel: no scaling, no orientation, colour space or alpha
 *  conversion. Needs createImageBitmap and OffscreenCanvas (a page or a worker). */
export async function reencodeAsPng(bytes: Uint8Array, mime: string): Promise<{png: Uint8Array; width: number; height: number}> {
    const bitmap = await createImageBitmap(new Blob([Uint8Array.from(bytes)], {type: mime}), {
        imageOrientation: 'none',
        premultiplyAlpha: 'none',
        colorSpaceConversion: 'none'
    });
    try {
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height),
            context = canvas.getContext('2d');
        if (!context) throw Error('2d context unavailable');
        context.drawImage(bitmap, 0, 0);
        const blob = await canvas.convertToBlob({type: 'image/png'});
        return {png: new Uint8Array(await blob.arrayBuffer()), width: bitmap.width, height: bitmap.height};
    } finally {
        bitmap.close();
    }
}
