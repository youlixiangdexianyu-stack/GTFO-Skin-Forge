export const pageRoutes = [
    {id: 'home', path: '/'},
    {id: 'library', path: '/models'},
    {id: 'model-workshop', path: '/model-workshop'},
    {id: 'workshop', path: '/workshop'},
    {id: 'enemy-editor', path: '/enemy-editor'},
    {id: 'room-editor', path: '/room-editor'},
    {id: 'map', path: '/map'},
    {id: 'level-guides', path: '/level-guides'},
    {id: 'level-guide-map', path: '/level-guide-map'},
    {id: 'logic', path: '/logic'},
    {id: 'audio', path: '/audio'},
    {id: 'docs', path: '/docs'},
    {id: 'community', path: '/community'},
    {id: 'lounge', path: '/lounge'},
    {id: 'tools', path: '/tools'},
    {id: 'install', path: '/install'},
    {id: 'account', path: '/account'},
    {id: 'admin', path: '/admin'},
    {id: 'settings', path: '/settings'},
    {id: 'privacy', path: '/privacy'},
    {id: 'help', path: '/help'},
    {id: 'forbidden', path: '/403'},
    {id: 'not-found', path: '/404'},
    {id: 'rate-limited', path: '/429'},
    {id: 'error', path: '/500'},
    {id: 'unavailable', path: '/503'},
    {id: 'maintenance', path: '/maintenance'}
] as const;
/** 页面底下带标识的子页：路由写法、所属的页、地址的样子。`/models/<模型标识>` 是模型库里一个模型的详情页，
 *  标识与模型文件用同一种写法（字母数字开头，其后字母数字、`_`、`-`）。 */
export const pageSubRoutes = [{route: '/models/$modelId', page: 'library', pattern: /^\/models\/[A-Za-z0-9][A-Za-z0-9_-]*$/}] as const;
export type PageId = (typeof pageRoutes)[number]['id'];
/** 路由文件的完整路径：每一页，加上各页带标识的子页。 */
export type PagePath = (typeof pageRoutes)[number]['path'] | (typeof pageSubRoutes)[number]['route'];
/** 地图的三个画面都在 `/map`：作品库（没有 `rundown`）、Rundown 页（`rundown`，`page` 是它的四页之一）、一关的编辑器
 *  （`rundown` 加 `expedition`）。`sourceExpedition` 是社区页按关卡 id 找回它所在的 Rundown。 */
export const rundownPages = ['general', 'levels', 'equipment', 'player'] as const;
export type RundownPage = (typeof rundownPages)[number];
/** 一关编辑器左栏的 8 页，也是地址里 `panel` 的取值（地图编辑器和行为编辑器都按它跳到某一页）。 */
export const mapPanels = ['rooms', 'briefing', 'startup', 'objectives', 'enemies', 'environment', 'triggers', 'check'] as const;
export type MapPanelId = (typeof mapPanels)[number];
export interface MapRepairLocationTarget {
    kind: 'fixed-item' | 'system-object' | 'native' | 'behavior';
    roomId?: string;
    objectId?: string;
    fieldPath: string;
}
function mapRepairLocationTarget(value: string | null): MapRepairLocationTarget | undefined {
    if (!value || value.length > 4096) return;
    try {
        const row = JSON.parse(value);
        if (
            !row ||
            typeof row !== 'object' ||
            Array.isArray(row) ||
            !['fixed-item', 'system-object', 'native', 'behavior'].includes(row.kind) ||
            typeof row.fieldPath !== 'string' ||
            !row.fieldPath ||
            row.fieldPath.length > 2048 ||
            (row.roomId !== undefined && typeof row.roomId !== 'string') ||
            (row.objectId !== undefined && typeof row.objectId !== 'string')
        )
            return;
        if (row.kind === 'fixed-item' && (!row.roomId || !row.objectId)) return;
        return {kind: row.kind, fieldPath: row.fieldPath, ...(row.roomId ? {roomId: row.roomId} : {}), ...(row.objectId ? {objectId: row.objectId} : {})};
    } catch {
        return;
    }
}
export function mapLocation(search: string) {
    const params = new URLSearchParams(search),
        documentId = params.get('rundown'),
        expeditionId = params.get('expedition'),
        sourceExpeditionId = params.get('sourceExpedition');
    const page = rundownPages.find(entry => entry === params.get('page')) ?? 'general';
    return {
        documentId,
        expeditionId,
        sourceExpeditionId,
        page,
        repairTarget: mapRepairLocationTarget(params.get('repairTarget')),
        screen: !documentId ? 'projects' : expeditionId ? 'editor' : 'rundown'
    };
}
export function mapLocationUrl(screen: string, documentId?: string, expeditionId?: string, page?: RundownPage, target?: MapRepairLocationTarget) {
    const params = new URLSearchParams();
    if (screen !== 'projects' && documentId) {
        // 本机存储的复合键留在存储层；地址只带作品 id。
        try {
            const key: unknown = JSON.parse(documentId);
            if (Array.isArray(key) && key.length === 2 && key[0] === null && typeof key[1] === 'string') documentId = key[1];
        } catch {
            /* 不是复合键：地址直接带原 id */
        }
        params.set('rundown', documentId);
        if (screen === 'editor' && expeditionId) {
            params.set('expedition', expeditionId);
            if (target) {
                const checked = mapRepairLocationTarget(JSON.stringify(target));
                if (checked) params.set('repairTarget', JSON.stringify(checked));
            }
        } else if (page && page !== 'general') params.set('page', page);
    }
    return '/map' + (params.size ? '?' + params : '');
}
/** 地址属于哪一页：页面本身，或这一页底下带标识的子页（模型详情属于模型库）。 */
export function pageOfPath(pathname: string) {
    const sub = pageSubRoutes.find(entry => entry.pattern.test(pathname));
    return pageRoutes.find(page => page.path === pathname) ?? (sub ? pageRoutes.find(page => page.id === sub.page) : undefined);
}
export function resolvePage(pathname: string) {
    const page = pageOfPath(pathname);
    if (page) return {file: 'app.html', path: page.path, redirect: false};
    return null;
}
