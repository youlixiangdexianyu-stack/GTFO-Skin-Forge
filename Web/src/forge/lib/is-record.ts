// A leaf module: it imports nothing, so the staged maintenance tools (Tools/Web/build-server.ts) can carry it as it is.
export const isRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
