/**
 * @typedef {Object} SelectedDirectory
 * @property {string} name
 * @property {Map<string, SelectedDirectory>} directories
 * @property {Map<string, File>} files
 */

/** @type {Map<string, SelectedDirectory>} */
const directories = new Map();
const selectedDirectories = new Map();
const pendingDirectoryUses = new Map();

function trackDirectory(directory) {
    const id = crypto.randomUUID();
    directories.set(id, directory);
    return id;
}

export function tryGetDirectory(id) {
    const [rootId, ...segments] = id.split('/');
    let directory = directories.get(rootId);
    for (const segment of segments) {
        directory = directory?.directories.get(segment);
    }
    return directory;
}

export function getDirectory(id) {
    const directory = tryGetDirectory(id);
    if (directory === undefined) {
        throw new Error(`The selected folder is no longer available, please select it again (${id}).`);
    }
    return directory;
}

export function setDirectory(id, directory) {
    directories.set(id, directory);
}

export function releaseDirectory(id) {
    directories.delete(id);
}

export function beginDirectoryUse(compilerKind, requestId, directoryId) {
    if (directoryId !== null) {
        pendingDirectoryUses.set(requestId, directoryId);
    }

    const previousId = selectedDirectories.get(compilerKind);
    if (directoryId === null) {
        selectedDirectories.delete(compilerKind);
    } else {
        selectedDirectories.set(compilerKind, directoryId);
    }
    return releaseUnusedDirectory(previousId);
}

export function endDirectoryUse(requestId) {
    const directoryId = pendingDirectoryUses.get(requestId);
    pendingDirectoryUses.delete(requestId);
    return releaseUnusedDirectory(directoryId);
}

function releaseUnusedDirectory(id) {
    if (id !== undefined &&
        ![...selectedDirectories.values()].includes(id) &&
        ![...pendingDirectoryUses.values()].includes(id)) {
        releaseDirectory(id);
        return id;
    }
    return null;
}

export async function pickDirectory(pickerId) {
    const input = document.createElement('input');
    input.type = 'file';
    if (!('webkitdirectory' in input)) {
        throw new Error('This browser does not support folder selection.');
    }
    input.id = pickerId;
    input.webkitdirectory = true;
    input.multiple = true;
    input.hidden = true;

    return new Promise((resolve, reject) => {
        input.addEventListener('cancel', () => {
            input.remove();
            resolve(null);
        }, { once: true });
        input.addEventListener('change', () => {
            try {
                const directory = createSelectedDirectory(input.files);
                resolve({ id: trackDirectory(directory), name: directory.name });
            } catch (error) {
                reject(error);
            } finally {
                input.remove();
            }
        }, { once: true });

        try {
            document.body.appendChild(input);
            input.click();
        } catch (error) {
            input.remove();
            reject(error);
        }
    });
}

function createDirectory(name) {
    return { name, directories: new Map(), files: new Map() };
}

function createSelectedDirectory(selectedFiles) {
    if (!selectedFiles?.length) {
        throw new Error('The selected folder contains no files. Select the repository folder containing the compiler build output.');
    }

    const rootName = selectedFiles[0].webkitRelativePath.split('/')[0];
    const root = createDirectory(rootName);
    for (const file of selectedFiles) {
        const segments = file.webkitRelativePath.split('/');
        if (segments.length < 2 || segments[0] !== rootName ||
            segments.some(segment => !segment || segment === '.' || segment === '..')) {
            throw new Error(`Invalid relative path in the selected folder: ${file.webkitRelativePath}`);
        }

        let directory = root;
        for (const segment of segments.slice(1, -1)) {
            let child = directory.directories.get(segment);
            if (child === undefined) {
                child = createDirectory(segment);
                directory.directories.set(segment, child);
            }
            directory = child;
        }
        directory.files.set(segments.at(-1), file);
    }
    return root;
}

export async function getSubdirectory(directoryId, segments) {
    let directory = getDirectory(directoryId);

    for (const segment of segments) {
        directory = directory.directories.get(segment);
        if (directory === undefined) {
            return null;
        }
        directoryId += `/${segment}`;
    }

    return directoryId;
}

export async function getDirectories(directoryId) {
    const directory = getDirectory(directoryId);

    const result = [];
    for (const entry of directory.directories.values()) {
        const id = `${directoryId}/${entry.name}`;
        result.push({ id, name: entry.name });
    }

    return result;
}

export function unwrapObject(obj) {
    return obj;
}

export async function getFileAsync(directoryId, fileName) {
    const file = getDirectory(directoryId).files.get(fileName);
    return file === undefined ? null : `${directoryId}/${fileName}`;
}

export async function getFileDataAsync(id) {
    const lastSlash = id.lastIndexOf('/');
    const file = getDirectory(id.slice(0, lastSlash)).files.get(id.slice(lastSlash + 1));
    if (file === undefined) {
        throw new Error(`The selected file is no longer available, please select the folder again (${id}).`);
    }
    return await file.arrayBuffer();
}

export function copyFileData(buffer, destination) {
    destination.set(new Uint8Array(buffer));
}
