import {
    Bone,
    BufferAttribute,
    BufferGeometry,
    Color,
    ColorSpace,
    Float32BufferAttribute,
    Group,
    LinearSRGBColorSpace,
    Matrix4,
    Mesh,
    MeshStandardMaterial,
    NoColorSpace,
    RepeatWrapping,
    Skeleton,
    SkinnedMesh,
    SRGBColorSpace,
    Texture,
    Uint16BufferAttribute
} from 'three';
import type {ImportedModelPackage} from '../../forge/imported-model.ts';
import {readImportedMesh} from '../../forge/imported-model-format.ts';

// 导入模型的唯一画法：直接用包里的 mesh 与贴图建 three 对象（不经 glb 再转一遍），所以看到的就是游戏读的那份字节。网站预览与
// ForgeSkin 编辑器（搬过来的人物包）共用。视口都是 glTF 右手系：包里是 Unity 左手系，这里把转换器做的三件事原样倒回去（X 取反、
// 绕向反过来、V 翻回去）。本文件只依赖 three 与格式读法。

/** The gear shader's surface map (metal in red, smoothness in green) as three.js reads one: roughness from green, metal from blue. */
function swapMetalSmooth(data: Uint8ClampedArray): void {
    for (let at = 0; at < data.length; at += 4) {
        const metal = data[at]!,
            smooth = data[at + 1]!;
        data[at] = 0;
        data[at + 1] = 255 - smooth;
        data[at + 2] = metal;
        data[at + 3] = 255;
    }
}

async function decodeTexture(bytes: Uint8Array, colorSpace: ColorSpace, remap?: (data: Uint8ClampedArray) => void): Promise<Texture> {
    const bitmap = await createImageBitmap(new Blob([Uint8Array.from(bytes)], {type: 'image/png'}), {premultiplyAlpha: 'none', colorSpaceConversion: 'none'});
    let source: ImageBitmap | OffscreenCanvas = bitmap;
    if (remap) {
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height),
            context = canvas.getContext('2d', {willReadFrequently: true});
        if (!context) throw Error('2d context unavailable');
        context.drawImage(bitmap, 0, 0);
        const image = context.getImageData(0, 0, bitmap.width, bitmap.height);
        remap(image.data);
        context.putImageData(image, 0, 0);
        source = canvas;
    }
    const texture = new Texture(source);
    texture.flipY = false;
    texture.colorSpace = colorSpace;
    texture.wrapS = texture.wrapT = RepeatWrapping;
    texture.needsUpdate = true;
    return texture;
}

export async function buildImportedScene(model: Pick<ImportedModelPackage, 'name' | 'descriptor' | 'mesh' | 'textures'>): Promise<Group> {
    const {positions, normals, uvs, indices, skin: weights} = readImportedMesh(model.mesh, model.descriptor);
    for (let vertex = 0; vertex < positions.length / 3; vertex++) {
        positions[vertex * 3] = -positions[vertex * 3]!;
        normals[vertex * 3] = -normals[vertex * 3]!;
    }
    for (let vertex = 0; vertex < uvs.length / 2; vertex++) uvs[vertex * 2 + 1] = 1 - uvs[vertex * 2 + 1]!;
    for (let at = 0; at < indices.length; at += 3) {
        const second = indices[at + 1]!;
        indices[at + 1] = indices[at + 2]!;
        indices[at + 2] = second;
    }
    const geometry = new BufferGeometry();
    geometry.setAttribute('position', new BufferAttribute(positions, 3));
    geometry.setAttribute('normal', new BufferAttribute(normals, 3));
    geometry.setAttribute('uv', new BufferAttribute(uvs, 2));
    geometry.setIndex(new BufferAttribute(indices, 1));
    for (const submesh of model.descriptor.submeshes) geometry.addGroup(submesh.start, submesh.count, submesh.material);
    // One picture can serve two roles (colour and normal), and the two read it in different colour spaces: a texture per picture and role.
    const decoded = new Map<string, Promise<Texture>>();
    const textureOf = (index: number, colorSpace: ColorSpace) => {
        if (index < 0) return Promise.resolve(null);
        const key = index + (colorSpace === SRGBColorSpace ? 's' : 'n');
        if (!decoded.has(key)) decoded.set(key, decodeTexture(model.textures[index]!, colorSpace));
        return decoded.get(key)!;
    };
    // The game reads metal in red and smoothness in green of one map; three.js reads metal from blue and roughness from green, so the map is rebuilt that way.
    const surfaces = new Map<number, Promise<Texture>>();
    const surfaceOf = (index: number) => {
        if (!surfaces.has(index)) surfaces.set(index, decodeTexture(model.textures[index]!, NoColorSpace, swapMetalSmooth));
        return surfaces.get(index)!;
    };
    const materials = await Promise.all(
        model.descriptor.materials.map(async material => {
            const surface = material.metalTexture >= 0 ? await surfaceOf(material.metalTexture) : null;
            return new MeshStandardMaterial({
                name: material.name,
                color: new Color().setRGB(material.baseColor[0], material.baseColor[1], material.baseColor[2], LinearSRGBColorSpace),
                map: await textureOf(material.texture, SRGBColorSpace),
                normalMap: await textureOf(material.normalTexture, NoColorSpace),
                emissive: new Color().setRGB(material.emissiveColor[0], material.emissiveColor[1], material.emissiveColor[2], LinearSRGBColorSpace),
                emissiveMap: await textureOf(material.emissiveTexture, SRGBColorSpace),
                metalness: material.metalTexture >= 0 ? 1 : material.metallic,
                roughness: material.metalTexture >= 0 ? 1 : material.roughness,
                metalnessMap: surface,
                roughnessMap: surface,
                alphaTest: material.alpha === 'mask' ? material.cutoff : 0
            });
        })
    );
    const scene = new Group(),
        skin = model.descriptor.skin;
    if (!skin || !weights) {
        const mesh = new Mesh(geometry, materials);
        mesh.name = model.name;
        scene.add(mesh);
        return scene;
    }
    // 蒙皮模型：骨架按描述符的静止姿态建（和网格同样 X 取反），网格放在最后；姿态由 imported-skin-pose.ts 按宿主骨驱动，没人驱动时就是绑定姿态。
    geometry.setAttribute('skinIndex', new Uint16BufferAttribute(weights.joints, 4));
    geometry.setAttribute('skinWeight', new Float32BufferAttribute(weights.weights, 4));
    const reflect = new Matrix4().makeScale(-1, 1, 1),
        rests = skin.bones.map(bone => reflect.clone().multiply(new Matrix4().fromArray(bone.rest)).multiply(reflect));
    const bones: Bone[] = [];
    for (const [index, row] of skin.bones.entries()) {
        const bone = new Bone(),
            local = row.parent >= 0 ? rests[row.parent]!.clone().invert().multiply(rests[index]!) : rests[index]!;
        bone.name = row.name;
        local.decompose(bone.position, bone.quaternion, bone.scale);
        (row.parent >= 0 ? bones[row.parent]! : scene).add(bone);
        bones.push(bone);
    }
    const skinned = new SkinnedMesh(geometry, materials);
    skinned.name = model.name;
    skinned.frustumCulled = false;
    scene.add(skinned);
    skinned.bind(
        new Skeleton(
            bones,
            rests.map(rest => rest.clone().invert())
        ),
        new Matrix4()
    );
    return scene;
}
