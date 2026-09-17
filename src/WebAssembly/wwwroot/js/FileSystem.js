/** @type {Map<string, FileSystemDirectoryHandle>} */
const directories = new Map();

function trackDirectory(handle) {
    const id = crypto.randomUUID();
    directories.set(id, handle);
    return id;
}

/** @type {Map<string, FileSystemFileHandle>} */
const files = new Map();

function trackFile(handle) {
    const id = crypto.randomUUID();
    files.set(id, handle);
    return id;
}

export async function pickDirectory(pickerId) {
    /** @type {FileSystemDirectoryHandle} */ let handle;
    try {
        handle = await window.showDirectoryPicker({ mode: 'read', id: pickerId });
    } catch (error) {
        if (error.name === 'AbortError') {
            return null;
        }
        throw error;
    }

    const id = trackDirectory(handle);
    return { id, name: handle.name };
}

export async function getSubdirectory(directoryId, segments) {
    let handle = directories.get(directoryId);
    for (const segment of segments) {
        try {
            handle = await handle.getDirectoryHandle(segment);
        } catch (error) {
            if (error.name === 'NotFoundError') {
                return null;
            }
            throw error;
        }
    }

    return trackDirectory(handle);
}

export async function getDirectories(directoryId) {
    const handle = directories.get(directoryId);

    const entries = [];
    for await (const entry of handle.values()) {
        if (entry.kind === 'directory') {
            entries.push(entry);
        }
    }

    const result = [];
    for (const entry of entries) {
        const id = trackDirectory(entry);
        result.push({ id, name: entry.name });
    }

    return result;
}

export function unwrapObject(obj) {
    return obj;
}

export async function getFileAsync(directoryId, fileName) {
    const handle = directories.get(directoryId);

    let fileHandle;
    try {
        fileHandle = await handle.getFileHandle(fileName);
    } catch (error) {
        if (error.name === 'NotFoundError') {
            return null;
        }
        throw error;
    }

    return trackFile(fileHandle);
}

export async function getFileDataAsync(id) {
    const handle = files.get(id);
    return await handle.getFile();
}
