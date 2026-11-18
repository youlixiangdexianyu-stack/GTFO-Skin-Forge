// Namespace names and route requirements; messages remain in Web/locales/<language>/*.json.
// Read off the module graph. The common set is every namespace the app shell's own modules name (the site frame, the
// status pages and the error copy any request can show); every page waits for it. A page then waits for `first`: every
// other namespace its route modules name statically, so all of its first screen. `later` is what only its dynamically
// loaded parts (editors opened from it, work checks, viewers) name: it is fetched as soon as the page shows, without
// holding it. A key whose namespace has not arrived asks for it (i18n.ts) and the copy re-renders when it lands.
// A door that reads another page's copy (the assistant's workshop catalogs and community search, the map workers)
// waits for that page's whole set (allNamespacesForPage).
import {pageOfPath} from '../../forge/page-routes.ts';
export const localeNamespaces =
    'account assistant audio audioName audioPicker authoringMessages behaviorModule behaviorNames common community contentPack controls customAudio customImage developerDocs dialog documents downloads editorShell enemy equipmentAuthoring fogHeight forge glance guideCombatData guideCombatLoadouts guideCombatProfiles guideHome guidePrimitives guideScene guideTerminal guideTerms help hub install legal levelGuide loading logic logicEditorTutorials logicShell lounge map mapAdd mapBalanceInventory mapBalanceUi mapCanvas mapCommunityImport mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapFlow mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapLocate mapMedia mapModelTransformCapabilities mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeEventIndexUi mapNativeFacilityObjectiveUi mapNativeGeneratorObjectiveUi mapNativeItemObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativePlacementUi mapNativeSceneScanUi mapNativeSetupUi mapNativeTimedObjectiveUi mapNativeTuning mapNavigation mapOutline mapPackagePreviewUi mapPackageReview mapPackageUi mapPlacedModel mapPlacement mapPreview mapProgression mapProject mapPublication mapReview mapRoomViewer mapRules mapShell mapStorage mapTerminalPicker mapTriggerZone mapViewport mapWaves mechanismTutorials messageNames modelWorkshop navigation notification packTest planCanvas projects publication publishedProjects request resourcePicker review roomEditorPage rundownEquipment settings sharedUi site status storageStatus textLanguageCapabilities toolbox tutorials useInLevel v1A1 v1A2 v1A3 v1B1 v1D1 v1D2 v1E2 webgl workshop'.split(
        ' '
    );

export const commonNamespaces =
    'assistant authoringMessages common community contentPack controls forge loading notification projects publishedProjects request site status'.split(' ');
const routes: Record<string, {first: string; later: string}> = {
    '/': {
        first: 'behaviorModule customAudio dialog documents editorShell enemy logic settings sharedUi storageStatus workshop',
        later: 'account behaviorNames customImage equipmentAuthoring glance logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapShell mapStorage mapTriggerZone mapWaves modelWorkshop review roomEditorPage'
    },
    '/account': {first: 'account dialog sharedUi', later: ''},
    '/admin': {
        first: 'behaviorModule customAudio customImage dialog documents downloads editorShell enemy equipmentAuthoring logic navigation publication roomEditorPage settings sharedUi workshop',
        later: 'account audio audioPicker behaviorNames glance logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapRoomViewer mapRules mapShell mapStorage mapTriggerZone mapWaves messageNames modelWorkshop packTest resourcePicker review storageStatus useInLevel webgl'
    },
    '/audio': {first: 'audio downloads roomEditorPage sharedUi', later: 'account dialog'},
    '/community': {
        first: 'audio behaviorModule customAudio customImage dialog documents downloads editorShell enemy equipmentAuthoring hub logic mapPublication navigation publication roomEditorPage settings sharedUi storageStatus useInLevel workshop',
        later: 'account audioPicker behaviorNames glance logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapReview mapRoomViewer mapRules mapShell mapStorage mapTriggerZone mapWaves messageNames modelWorkshop packTest resourcePicker review webgl'
    },
    '/docs': {first: 'developerDocs enemy logic map mapPackageUi', later: ''},
    '/enemy-editor': {
        first: 'behaviorModule behaviorNames customImage dialog documents editorShell enemy equipmentAuthoring glance logic logicShell map mapPackageUi mapProject mapPublication review roomEditorPage settings sharedUi storageStatus useInLevel workshop',
        later: 'account audio audioPicker customAudio logicEditorTutorials mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPlacedModel mapPlacement mapReview mapRules mapShell mapStorage mapTriggerZone mapWaves messageNames modelWorkshop packTest resourcePicker webgl'
    },
    '/help': {first: 'help', later: ''},
    '/install': {first: 'enemy install logic map', later: 'account dialog'},
    '/level-guide-map': {
        first: 'behaviorModule behaviorNames customImage editorShell enemy equipmentAuthoring glance guideHome guidePrimitives guideScene levelGuide logic logicShell map mapDoorInspector mapExport mapExtraction mapGameUi mapGtfoChecks mapInspector mapLevelObjects mapNativeChecks mapNativeObjectiveUi mapNativeSceneScanUi mapPackageUi mapPlacement mapProject mapPublication mapReview mapShell planCanvas review roomEditorPage workshop',
        later: 'account customAudio dialog documents fogHeight mapAdd mapEditor mapEnvironment mapFeatures mapInteractionPoint mapLevel mapMedia mapNativeAudio mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapOutline mapPlacedModel mapRoomViewer mapTriggerZone mapViewport mapWaves modelWorkshop settings sharedUi webgl'
    },
    '/level-guides': {
        first: 'guideHome levelGuide',
        later: 'behaviorModule behaviorNames enemy equipmentAuthoring glance guideCombatData guideCombatLoadouts guidePrimitives guideTerminal logic logicShell map mapPackageUi mapPlacement mapProject review roomEditorPage workshop'
    },
    '/logic': {
        first: 'behaviorModule behaviorNames customAudio customImage dialog documents editorShell enemy equipmentAuthoring glance logic logicEditorTutorials logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapShell mapTriggerZone mapWaves messageNames resourcePicker review roomEditorPage settings sharedUi storageStatus useInLevel workshop',
        later: 'account audio audioPicker mapRules mapStorage modelWorkshop packTest'
    },
    '/lounge': {
        first: 'editorShell enemy logic lounge roomEditorPage sharedUi',
        later: 'account behaviorModule behaviorNames customImage dialog documents equipmentAuthoring glance logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapShell mapStorage mapTriggerZone mapWaves modelWorkshop review settings storageStatus workshop'
    },
    '/map': {
        first: 'behaviorModule behaviorNames customImage dialog documents editorShell enemy equipmentAuthoring glance logic logicShell map mapAdd mapCanvas mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapRoomViewer mapShell mapStorage mapTriggerZone mapWaves review roomEditorPage settings sharedUi storageStatus workshop',
        later: 'account audio audioPicker customAudio fogHeight help logicEditorTutorials mapBalanceInventory mapBalanceUi mapCommunityImport mapFlow mapLocate mapNativeEventIndexUi mapNativeFacilityObjectiveUi mapNativeItemObjectiveUi mapNativePlacementUi mapNativeSetupUi mapNativeTimedObjectiveUi mapNativeTuning mapNavigation mapPackagePreviewUi mapPackageReview mapPreview mapProgression mapRules mapTerminalPicker mapViewport messageNames modelWorkshop packTest resourcePicker rundownEquipment useInLevel webgl'
    },
    '/model-workshop': {
        first: 'behaviorModule behaviorNames customAudio customImage dialog documents editorShell enemy equipmentAuthoring glance logic logicShell map mapDoorInspector mapExport mapExtraction mapGameUi mapGtfoChecks mapInspector mapLevelObjects mapNativeChecks mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapPackageUi mapPlacement mapProject mapPublication mapReview mapShell modelWorkshop packTest review roomEditorPage settings sharedUi storageStatus useInLevel webgl workshop',
        later: 'account mapAdd mapEditor mapEnvironment mapFeatures mapInteractionPoint mapLevel mapMedia mapNativeAudio mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapOutline mapPlacedModel mapStorage mapTriggerZone mapWaves'
    },
    '/models': {
        first: 'downloads logic mapPlacement modelWorkshop review roomEditorPage sharedUi webgl',
        later: 'account behaviorModule behaviorNames customAudio customImage dialog documents enemy equipmentAuthoring glance logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapProject mapPublication mapReview mapRoomViewer mapShell mapTriggerZone mapWaves settings workshop'
    },
    '/privacy': {first: 'legal', later: ''},
    '/room-editor': {
        first: 'behaviorModule behaviorNames customImage dialog documents editorShell enemy equipmentAuthoring glance logic logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapShell mapTriggerZone mapWaves review roomEditorPage settings sharedUi storageStatus useInLevel workshop',
        later: 'account customAudio mapRoomViewer mapStorage modelWorkshop packTest webgl'
    },
    '/settings': {
        first: 'behaviorModule behaviorNames dialog documents editorShell enemy equipmentAuthoring logic logicShell lounge map mapPackageUi mapProject review settings sharedUi workshop',
        later: 'account customImage glance mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPlacedModel mapPlacement mapPublication mapReview mapShell mapStorage mapTriggerZone mapWaves modelWorkshop roomEditorPage storageStatus'
    },
    '/tools': {
        first: 'levelGuide toolbox',
        later: 'behaviorModule behaviorNames customImage documents editorShell enemy equipmentAuthoring glance guideCombatData logic logicShell map mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPackageUi mapPlacedModel mapPlacement mapProject mapPublication mapReview mapShell mapStorage mapTriggerZone mapWaves review roomEditorPage settings storageStatus workshop'
    },
    '/workshop': {
        first: 'behaviorModule behaviorNames dialog documents editorShell enemy equipmentAuthoring glance logic logicShell map mapPackageUi mapPlacement mapProject roomEditorPage settings sharedUi storageStatus useInLevel workshop',
        later: 'account audio audioPicker customAudio customImage downloads logicEditorTutorials mapAdd mapDoorInspector mapEditor mapEnvironment mapExport mapExtraction mapFeatures mapGameUi mapGtfoChecks mapInspector mapInteractionPoint mapLevel mapLevelObjects mapMedia mapNativeAudio mapNativeChecks mapNativeCommandUi mapNativeCommands mapNativeGeneratorObjectiveUi mapNativeLibrary mapNativeObjectiveUi mapNativeSceneScanUi mapOutline mapPlacedModel mapPublication mapReview mapRules mapShell mapStorage mapTriggerZone mapWaves messageNames modelWorkshop packTest resourcePicker review webgl'
    }
};
const words = (text: string | undefined) => (text ? text.split(' ') : []);
const routeOf = (pathname: string) => routes[pageOfPath(pathname)?.path ?? pathname];
/** What a page waits for before it shows. */
export function namespacesForPage(pathname: string): readonly string[] {
    return [...new Set([...commonNamespaces, ...words(routeOf(pathname)?.first)])];
}
/** What a page's dynamically loaded parts add; fetched once the page shows. */
export function laterNamespacesForPage(pathname: string): readonly string[] {
    return words(routeOf(pathname)?.later);
}
/** Everything a page can show, for doors and workers that run its code outside it. */
export function allNamespacesForPage(pathname: string): readonly string[] {
    return [...namespacesForPage(pathname), ...laterNamespacesForPage(pathname)];
}
