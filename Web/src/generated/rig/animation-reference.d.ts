import type { Object3D } from 'three';
export interface RigVector { readonly x: number; readonly y: number; readonly z: number; }
export interface RigQuaternion extends RigVector { readonly w: number; }
export interface FingerRig { readonly joints: readonly string[]; readonly positions: Readonly<Record<string, RigVector>>; readonly rotations: Readonly<Record<string, RigQuaternion>>; }
export declare const HumanJoints: readonly string[];
export declare const HumanFingerJoints: readonly string[];
export declare function construct(skeleton: {joints: Record<string, Object3D>}, fingers?: FingerRig): void;
