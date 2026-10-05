// Start screen for playing a folder on this computer: choose a folder, or resume the remembered one.

import {
  canPickFolder, contentsFromFiles, contentsFromFolder, loadFolder, pickFolder, saveFolder, type FsDirHandle,
} from '../../media/folder-source.js';
import type { SlideshowContents } from '../../media/media-types.js';
import { query } from '../../utils/dom.js';

export interface FolderPickerHandle {
  /** Shows the screen again (e.g. after "Change folder"). */
  show(): void;
}

/** Calls `onContents` with the chosen folder's contents; the screen hides itself when there is something to play. */
export async function mountFolderPicker(root: HTMLElement, onContents: (contents: SlideshowContents) => void): Promise<FolderPickerHandle> {
  const message = query(root, '.start-screen__message');
  const resumeBtn = query<HTMLButtonElement>(root, '.start-screen__resume');
  const pickBtn = query<HTMLButtonElement>(root, '.start-screen__pick');
  const fallback = query<HTMLInputElement>(root, '.start-screen__fallback');

  const begin = (contents: SlideshowContents) => {
    if (!contents.items.length) {
      message.textContent = 'No images or videos found in that folder. Choose another one.';
      root.hidden = false;
      return;
    }
    root.hidden = true;
    onContents(contents);
  };
  const openFolder = async (dir: FsDirHandle) => begin(await contentsFromFolder(dir));

  pickBtn.onclick = async () => {
    if (!canPickFolder()) { fallback.click(); return; }
    try {
      const dir = await pickFolder();
      await saveFolder(dir);
      await openFolder(dir);
    } catch (err) {
      if ((err as DOMException).name !== 'AbortError') message.textContent = `Could not open that folder: ${(err as Error).message}`;
    }
  };
  fallback.onchange = async () => { if (fallback.files) begin(await contentsFromFiles(fallback.files)); };

  const handle: FolderPickerHandle = {
    show() {
      resumeBtn.hidden = true;
      message.textContent = 'Choose the folder that holds your photos and videos.';
      root.hidden = false;
    },
  };

  // Remembered folder: start straight away if the browser still grants access,
  // otherwise one click re-grants it (browsers require a click for that).
  const saved = canPickFolder() ? await loadFolder() : undefined;
  if (!saved) return handle;
  if (await saved.queryPermission({ mode: 'read' }) === 'granted') { await openFolder(saved); return handle; }
  resumeBtn.textContent = `Resume "${saved.name}"`;
  resumeBtn.hidden = false;
  pickBtn.classList.remove('button--primary');
  pickBtn.textContent = 'Choose a different folder';
  resumeBtn.onclick = async () => {
    if (await saved.requestPermission({ mode: 'read' }) === 'granted') await openFolder(saved);
  };
  return handle;
}
