import {z} from 'zod';

/** 公开到社区时的权利声明（合同 §3.2「公开到社区」）：上传者是原作者本人，或已获得原作者许可（写明原作者与许可来源）。
 *  音频、图片、动作、模型、geomorph 来源与作品都用这一份。 */
export const resourceRightsLimits = {originalAuthor: 120, permissionSource: 500} as const;
const field = (max: number) =>
    z
        .string()
        .trim()
        .min(1)
        .max(max)
        .refine(value => !/[\u0000-\u001f\u007f]/.test(value));
export const resourceRightsSchema = z.discriminatedUnion('statement', [
    z.object({statement: z.literal('original-author')}).strict(),
    z
        .object({
            statement: z.literal('permission'),
            originalAuthor: field(resourceRightsLimits.originalAuthor),
            permissionSource: field(resourceRightsLimits.permissionSource)
        })
        .strict()
]);
export type ResourceRights = z.infer<typeof resourceRightsSchema>;

/** 读一份权利声明；形状不对、许可缺原作者或来源都返回 undefined。 */
export function readResourceRights(value: unknown): ResourceRights | undefined {
    const parsed = resourceRightsSchema.safeParse(value);
    return parsed.success ? parsed.data : undefined;
}
