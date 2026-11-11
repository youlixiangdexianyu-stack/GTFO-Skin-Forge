// Plain-language messages for backend error codes. Each says what happened and what to do next.
import type { BackendError } from './protocol';

const MESSAGES: Record<string, string> = {
  'game-not-found': '没找到 GTFO。点右上角的游戏状态，手动选一下游戏所在的文件夹。',
  'game-running': 'GTFO 正在运行，插件文件被占用。先退出游戏再点一次；只更新皮肤的话不用退出。',
  'not-found': '要用的文件不见了，可能被移动或删除。重新选择一下贴图或模型。',
  'missing-file': '有贴图文件找不到了。检查一下贴图文件夹，或在右侧重新选择标红的贴图。',
  'bad-path': '有文件路径无法使用。把贴图放到普通文件夹里再试一次。',
  'bad-mesh': '这个模型文件读不了。请从 Blender 等软件重新导出为 .glb 再导入。',
  'bad-screen': '屏幕设置有问题，尺寸不能为 0。检查一下屏幕的宽和高。',
  'no-glyphs': '屏幕的数字字体没生成出来。换一个字体再试一次。',
  'bad-atlas': '动态贴图的帧没生成出来。检查一下视频或图片能否正常打开。',
  'no-atlas': '动态贴图的帧没生成出来。检查一下视频或图片能否正常打开。',
  'empty-attachment': '有一个挂件既没有模型也没有屏幕。给它导入模型、开启屏幕，或删掉它。',
  'no-anchor': '有挂件没有挂在任何部件上。选中它，在右侧选一个挂载部件。',
  'no-material': '还没有替换任何贴图。拖入一个贴图文件夹再试。',
  'backend-exited': '后台服务意外退出，正在重启。几秒后再试一次。',
};

export function friendly(e: unknown, fallback = '操作没有完成。再试一次；如果反复出现，打开日志看看详情。'): string {
  const err = e as Partial<BackendError> | undefined;
  if (err?.code && MESSAGES[err.code]) return MESSAGES[err.code];
  return fallback;
}

export function detail(e: unknown): string {
  const err = e as Partial<BackendError & Error> | undefined;
  return err?.code ? `${err.code}: ${err.message}` : String(err?.message ?? e);
}
