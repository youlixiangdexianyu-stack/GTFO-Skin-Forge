export const HumanJoints = [
    "hip",
    "leftUpperLeg",
    "leftLowerLeg",
    "leftFoot",
    "rightUpperLeg",
    "rightLowerLeg",
    "rightFoot",
    "spine0",
    "spine1",
    "spine2",
    "leftShoulder",
    "leftUpperArm",
    "leftLowerArm",
    "leftHand",
    "rightShoulder",
    "rightUpperArm",
    "rightLowerArm",
    "rightHand",
    "neck",
    "head",
];
export const defaultHumanStructure = { hip: { x: 0, y: 1.04, z: 0 }, leftUpperLeg: { x: 0.09999999, y: 0, z: 0 }, leftLowerLeg: { x: 1.776357E-17, y: -0.49, z: 0 }, leftFoot: { x: 0, y: -0.44, z: 0 }, rightUpperLeg: { x: -0.09999999, y: 1.421085E-16, z: 0 }, rightLowerLeg: { x: -1.776357E-17, y: -0.49, z: 0 }, rightFoot: { x: -1.887379E-17, y: -0.44, z: 0 }, spine0: { x: 0, y: 0.11, z: 0 }, spine1: { x: 0, y: 0.11, z: 0 }, spine2: { x: 0, y: 0.11, z: 0 }, leftShoulder: { x: 0.03, y: 0.19, z: 0 }, leftUpperArm: { x: 0.18, y: -2.842171E-16, z: 0 }, leftLowerArm: { x: 0.33, y: -4.662936E-16, z: 0 }, leftHand: { x: 0.29, y: 2.431388E-16, z: -9.769963E-17 }, rightShoulder: { x: -0.03, y: 0.19, z: 0 }, rightUpperArm: { x: -0.18, y: 0, z: 0 }, rightLowerArm: { x: -0.33, y: 0, z: 0 }, rightHand: { x: -0.29, y: -2.842171E-16, z: 0 }, neck: { x: 0, y: 0.275, z: 0.02499937 }, head: { x: 0, y: 0.09999999, z: 0 } };
export const defaultHumanPose = { hip: { x: 0, y: 0, z: 0, w: 1 }, leftUpperLeg: { x: 0, y: 0, z: 0.02617695, w: 0.9996573 }, leftLowerLeg: { x: 0.04361939, y: 0, z: 0, w: 0.9990482 }, leftFoot: { x: 0.2756374, y: 0, z: 0, w: 0.9612617 }, rightUpperLeg: { x: 0, y: 0, z: -0.02617695, w: 0.9996573 }, rightLowerLeg: { x: 0.04361939, y: 0, z: 0, w: 0.9990482 }, rightFoot: { x: 0.2756374, y: 0, z: 0, w: 0.9612617 }, spine0: { x: 0, y: 0, z: 0, w: 1 }, spine1: { x: 0, y: 0, z: 0, w: 1 }, spine2: { x: 0, y: 0, z: 0, w: 1 }, leftShoulder: { x: 0, y: 0, z: 0, w: 1 }, leftUpperArm: { x: 0, y: 0, z: -0.5446391, w: 0.8386706 }, leftLowerArm: { x: 0, y: -0.199368, z: 0, w: 0.9799247 }, leftHand: { x: 0.004106232, y: 0.07835157, z: -0.05217462, w: 0.9955511 }, rightShoulder: { x: 0, y: 0, z: 0, w: 1 }, rightUpperArm: { x: 0, y: 0, z: 0.5446391, w: 0.8386706 }, rightLowerArm: { x: 0, y: 0.199368, z: 0, w: 0.9799247 }, rightHand: { x: 0.07835157, y: 0.004106232, z: -0.9955511, w: 0.05217462 }, neck: { x: 0.1736482, y: 0, z: 0, w: 0.9848078 }, head: { x: -0.1736482, y: 0, z: 0, w: 0.9848078 } };
export const HumanFingerJoints = ["leftThumb1", "leftThumb2", "leftThumb3", "leftIndex1", "leftIndex2", "leftIndex3", "leftMiddle1", "leftMiddle2", "leftMiddle3", "leftRing1", "leftRing2", "leftRing3", "leftPinky1", "leftPinky2", "leftPinky3", "rightThumb1", "rightThumb2", "rightThumb3", "rightIndex1", "rightIndex2", "rightIndex3", "rightMiddle1", "rightMiddle2", "rightMiddle3", "rightRing1", "rightRing2", "rightRing3", "rightPinky1", "rightPinky2", "rightPinky3"];
export const fingerParents = { "leftThumb1": "leftHand", "leftThumb2": "leftThumb1", "leftThumb3": "leftThumb2", "leftIndex1": "leftHand", "leftIndex2": "leftIndex1", "leftIndex3": "leftIndex2", "leftMiddle1": "leftHand", "leftMiddle2": "leftMiddle1", "leftMiddle3": "leftMiddle2", "leftRing1": "leftHand", "leftRing2": "leftRing1", "leftRing3": "leftRing2", "leftPinky1": "leftHand", "leftPinky2": "leftPinky1", "leftPinky3": "leftPinky2", "rightThumb1": "rightHand", "rightThumb2": "rightThumb1", "rightThumb3": "rightThumb2", "rightIndex1": "rightHand", "rightIndex2": "rightIndex1", "rightIndex3": "rightIndex2", "rightMiddle1": "rightHand", "rightMiddle2": "rightMiddle1", "rightMiddle3": "rightMiddle2", "rightRing1": "rightHand", "rightRing2": "rightRing1", "rightRing3": "rightRing2", "rightPinky1": "rightHand", "rightPinky2": "rightPinky1", "rightPinky3": "rightPinky2" };
export function construct(skeleton, fingers) {
    skeleton.joints.hip.add(skeleton.joints.spine0, skeleton.joints.leftUpperLeg, skeleton.joints.rightUpperLeg);
    skeleton.joints.leftUpperLeg.add(skeleton.joints.leftLowerLeg);
    skeleton.joints.leftLowerLeg.add(skeleton.joints.leftFoot);
    skeleton.joints.rightUpperLeg.add(skeleton.joints.rightLowerLeg);
    skeleton.joints.rightLowerLeg.add(skeleton.joints.rightFoot);
    skeleton.joints.spine0.add(skeleton.joints.spine1);
    skeleton.joints.spine1.add(skeleton.joints.spine2);
    skeleton.joints.spine2.add(skeleton.joints.neck, skeleton.joints.leftShoulder, skeleton.joints.rightShoulder);
    skeleton.joints.leftShoulder.add(skeleton.joints.leftUpperArm);
    skeleton.joints.leftUpperArm.add(skeleton.joints.leftLowerArm);
    skeleton.joints.leftLowerArm.add(skeleton.joints.leftHand);
    skeleton.joints.rightShoulder.add(skeleton.joints.rightUpperArm);
    skeleton.joints.rightUpperArm.add(skeleton.joints.rightLowerArm);
    skeleton.joints.rightLowerArm.add(skeleton.joints.rightHand);
    skeleton.joints.neck.add(skeleton.joints.head);
    for (const joint of HumanJoints) {
        skeleton.joints[joint].position.copy(defaultHumanStructure[joint]);
        skeleton.joints[joint].quaternion.copy(defaultHumanPose[joint]);
    }
    if (fingers)
        for (const joint of fingers.joints) {
            const parent = fingerParents[joint];
            skeleton.joints[parent].add(skeleton.joints[joint]);
            skeleton.joints[joint].position.copy(fingers.positions[joint]);
            skeleton.joints[joint].quaternion.copy(fingers.rotations[joint]);
        }
}
