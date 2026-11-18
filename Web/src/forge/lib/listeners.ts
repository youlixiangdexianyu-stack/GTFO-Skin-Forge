/** One subscriber list: `subscribe` returns its unsubscribe (the shape useSyncExternalStore wants); `emit` calls every listener. */
export function createListeners<A extends unknown[] = []>() {
    const listeners = new Set<(...args: A) => void>();
    return {
        subscribe(listener: (...args: A) => void): () => void {
            listeners.add(listener);
            return () => {
                listeners.delete(listener);
            };
        },
        emit(...args: A): void {
            for (const listener of [...listeners]) listener(...args);
        },
        /** How many listeners are subscribed (a store that releases itself when the last one leaves). */
        get size(): number {
            return listeners.size;
        },
        /** Drops every listener (a store that is disposed). */
        clear(): void {
            listeners.clear();
        }
    };
}
