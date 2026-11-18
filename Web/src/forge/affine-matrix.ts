/** Column-major 4x4 matrix as 16 numbers, the layout Three.js Matrix4.elements uses. */
export type Matrix16 = number[];

/** Unit quaternion (x,y,z,w) of Euler radians in XYZ order, term for term as Three.js Quaternion.setFromEuler. */
export function eulerQuaternion([x, y, z]: readonly number[]): [number, number, number, number] {
    const c1 = Math.cos(x! / 2),
        c2 = Math.cos(y! / 2),
        c3 = Math.cos(z! / 2),
        s1 = Math.sin(x! / 2),
        s2 = Math.sin(y! / 2),
        s3 = Math.sin(z! / 2);
    return [s1 * c2 * c3 + c1 * s2 * s3, c1 * s2 * c3 - s1 * c2 * s3, c1 * c2 * s3 + s1 * s2 * c3, c1 * c2 * c3 - s1 * s2 * s3];
}

export const identityMatrix = (): Matrix16 => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

/** Translation * rotation (unit quaternion x,y,z,w) * scale; the same arithmetic order as Three.js Matrix4.compose. */
export function composeMatrix(position: readonly number[], rotation: readonly number[], scale: readonly number[]): Matrix16 {
    const [x, y, z, w] = rotation,
        x2 = x + x,
        y2 = y + y,
        z2 = z + z;
    const xx = x * x2,
        xy = x * y2,
        xz = x * z2,
        yy = y * y2,
        yz = y * z2,
        zz = z * z2,
        wx = w * x2,
        wy = w * y2,
        wz = w * z2;
    const [sx, sy, sz] = scale;
    return [
        (1 - (yy + zz)) * sx,
        (xy + wz) * sx,
        (xz - wy) * sx,
        0,
        (xy - wz) * sy,
        (1 - (xx + zz)) * sy,
        (yz + wx) * sy,
        0,
        (xz + wy) * sz,
        (yz - wx) * sz,
        (1 - (xx + yy)) * sz,
        0,
        position[0],
        position[1],
        position[2],
        1
    ];
}

/** Determinant with the same term order as Three.js Matrix4.determinant. */
export function matrixDeterminant(m: readonly number[]): number {
    const [n11, n21, n31, n41, n12, n22, n32, n42, n13, n23, n33, n43, n14, n24, n34, n44] = m as [
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number,
        number
    ];
    return (
        n41 * (+n14 * n23 * n32 - n13 * n24 * n32 - n14 * n22 * n33 + n12 * n24 * n33 + n13 * n22 * n34 - n12 * n23 * n34) +
        n42 * (+n11 * n23 * n34 - n11 * n24 * n33 + n14 * n21 * n33 - n13 * n21 * n34 + n13 * n24 * n31 - n14 * n23 * n31) +
        n43 * (+n11 * n24 * n32 - n11 * n22 * n34 - n14 * n21 * n32 + n12 * n21 * n34 + n14 * n22 * n31 - n12 * n24 * n31) +
        n44 * (-n13 * n22 * n31 - n11 * n23 * n32 + n11 * n22 * n33 + n13 * n21 * n32 - n12 * n21 * n33 + n12 * n23 * n31)
    );
}

/** a * b, summed in the same order as Three.js Matrix4.multiplyMatrices. */
export function multiplyMatrices(a: readonly number[], b: readonly number[]): Matrix16 {
    const result: Matrix16 = new Array(16);
    for (let column = 0; column < 4; column++)
        for (let row = 0; row < 4; row++)
            result[column * 4 + row] =
                a[row]! * b[column * 4]! + a[4 + row]! * b[column * 4 + 1]! + a[8 + row]! * b[column * 4 + 2]! + a[12 + row]! * b[column * 4 + 3]!;
    return result;
}

/** A column-major 4×4 applied to a point. */
export const transformPoint = (m: readonly number[], [x, y, z]: readonly number[]): [number, number, number] => [
    m[0]! * x! + m[4]! * y! + m[8]! * z! + m[12]!,
    m[1]! * x! + m[5]! * y! + m[9]! * z! + m[13]!,
    m[2]! * x! + m[6]! * y! + m[10]! * z! + m[14]!
];

/** The 3×3 part's cofactor matrix (det·M⁻ᵀ, row-major over its column-major source) and determinant. */
export function cofactors3(m: ArrayLike<number>): {det: number; c: number[]} {
    const a = m[0]!,
        b = m[1]!,
        c = m[2]!,
        d = m[4]!,
        e = m[5]!,
        f = m[6]!,
        g = m[8]!,
        h = m[9]!,
        i = m[10]!;
    return {
        det: a * (e * i - f * h) - d * (b * i - c * h) + g * (b * f - c * e),
        c: [e * i - f * h, -(d * i - f * g), d * h - e * g, -(b * i - c * h), a * i - c * g, -(a * h - b * g), b * f - c * e, -(a * f - c * d), a * e - b * d]
    };
}

/** The inverse of a column-major affine 4×4 (last row 0 0 0 1); null when its 3×3 part is singular. */
export function invertAffine(m: ArrayLike<number>): Matrix16 | null {
    const {det, c} = cofactors3(m);
    if (!(Math.abs(det) > 1e-12)) return null;
    const r = [c[0]!, c[3]!, c[6]!, c[1]!, c[4]!, c[7]!, c[2]!, c[5]!, c[8]!].map(v => v / det);
    const x = m[12]!,
        y = m[13]!,
        z = m[14]!;
    return [
        r[0]!,
        r[1]!,
        r[2]!,
        0,
        r[3]!,
        r[4]!,
        r[5]!,
        0,
        r[6]!,
        r[7]!,
        r[8]!,
        0,
        -(r[0]! * x + r[3]! * y + r[6]! * z),
        -(r[1]! * x + r[4]! * y + r[7]! * z),
        -(r[2]! * x + r[5]! * y + r[8]! * z),
        1
    ];
}
