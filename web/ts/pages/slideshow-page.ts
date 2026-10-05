// Entry point for slideshow.html: plays a folder from this computer.
// (The album page will reuse mountSlideshow with contents from the API.)

import { mountFolderPicker } from '../components/slideshow/folder-picker.js';
import { mountSlideshow } from '../components/slideshow/slideshow.js';
import { query } from '../utils/dom.js';

const root = query(document, '.slideshow');
let picker: { show(): void } | undefined;
const slideshow = mountSlideshow(root, { onChangeSource: () => picker?.show() });
picker = await mountFolderPicker(query(root, '.start-screen'), contents => slideshow.play(contents));
