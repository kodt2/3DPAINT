import * as THREE from '/lib/three/three.module.js';
import { GLTFLoader } from '/lib/three/GLTFLoader.js';

const appShell = document.getElementById('appShell');
const sidebarToggle = document.getElementById('sidebarToggle');
const assetGrid = document.getElementById('assetGrid');
const uploadDrawer = document.getElementById('uploadDrawer');
const uploadOpenBtn = document.getElementById('uploadOpenBtn');
const uploadCloseBtn = document.getElementById('uploadCloseBtn');
const dropZone = document.getElementById('dropZone');
const uploadInput = document.getElementById('uploadInput');
const validationList = document.getElementById('validationList');
const uploadPipeline = document.getElementById('uploadPipeline');

const maxSizeBytes = 500 * 1024 * 1024;
const allowedExtensions = new Set(['.stl', '.obj', '.step', '.stp','.glb']);

const sceneMap = new Map();

sidebarToggle?.addEventListener('click', () => {
    appShell.classList.toggle('sidebar-collapsed');
});

uploadOpenBtn?.addEventListener('click', () => {
    uploadDrawer.classList.add('open');
    uploadDrawer.setAttribute('aria-hidden', 'false');
});

uploadCloseBtn?.addEventListener('click', () => {
    uploadDrawer.classList.remove('open');
    uploadDrawer.setAttribute('aria-hidden', 'true');
    uploadPipeline?.classList.add('hidden');
});

uploadInput?.addEventListener('change', async (event) => {
    const file = event.target.files?.[0];
    if (file) {
        await validateAndUpload(file);
    }
    uploadInput.value = '';
});

['dragenter', 'dragover'].forEach((eventName) => {
    dropZone?.addEventListener(eventName, (event) => {
        event.preventDefault();
        dropZone.classList.add('drag-over');
    });
});

['dragleave', 'dragend', 'drop'].forEach((eventName) => {
    dropZone?.addEventListener(eventName, (event) => {
        event.preventDefault();
        if (eventName !== 'drop') {
            dropZone.classList.remove('drag-over');
        }
    });
});

dropZone?.addEventListener('drop', async (event) => {
    dropZone.classList.remove('drag-over');
    const file = event.dataTransfer?.files?.[0];
    if (file) {
        await validateAndUpload(file);
    }
});

async function validateAndUpload(file) {
    clearValidation();

    const extension = file.name.includes('.') ? `.${file.name.split('.').pop().toLowerCase()}` : '';
    const isExtensionValid = allowedExtensions.has(extension);
    const isSizeValid = file.size > 0 && file.size <= maxSizeBytes;

    appendValidation(isExtensionValid, isExtensionValid
        ? `Формат поддерживается: ${extension.toUpperCase().replace('.', '')}`
        : `Недопустимый формат: ${extension || 'без расширения'}`);

    appendValidation(isSizeValid, isSizeValid
        ? `Размер файла: ${(file.size / 1024 / 1024).toFixed(2)} MB`
        : 'Размер должен быть от 1 байта до 500 MB');

    if (!isExtensionValid || !isSizeValid) {
        return;
    }

    uploadPipeline?.classList.remove('hidden');

    try {
        setPipelineStep('upload');
        const formData = new FormData();
        formData.append('file', file);

        const response = await fetch('/api/files/upload', {
            method: 'POST',
            body: formData
        });

        if (!response.ok) {
            const error = await response.json();
            throw new Error(error.message || 'Ошибка загрузки файла');
        }

        setPipelineStep('analysis');
        await wait(450);
        setPipelineStep('preview');

        await wait(450);
        appendValidation(true, 'Файл успешно загружен, превью создано.');
        await loadFiles();
    } catch (error) {
        appendValidation(false, error.message || 'Ошибка загрузки');
    } finally {
        setTimeout(() => uploadPipeline?.classList.add('hidden'), 900);
    }
}

function appendValidation(ok, text) {
    const item = document.createElement('div');
    item.className = `validation-item ${ok ? 'ok' : 'error'}`;
    item.textContent = `${ok ? '✅' : '⚠️'} ${text}`;
    validationList?.appendChild(item);
}

function clearValidation() {
    if (validationList) {
        validationList.innerHTML = '';
    }
}

function setPipelineStep(activeStep) {
    document.querySelectorAll('[data-step]').forEach((step) => {
        step.classList.toggle('active', step.getAttribute('data-step') === activeStep);
    });
}

function wait(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

function createAssetCard(item, idx) {
    const card = document.createElement('article');
    card.className = 'asset-card glass-panel';
    card.innerHTML = `
        <div class="asset-preview" data-card-index="${idx}" data-url="${item.previewUrl}">
            ${item.thumbnailUrl
                ? `<img class="asset-thumb" src="${item.thumbnailUrl}" alt="${item.fileName}" />`
                : '<div class="asset-skeleton"></div>'}
        </div>
        <div class="asset-meta">
            <input class="asset-name" value="${item.fileName}" aria-label="Inline file name" readonly />
            <div class="asset-badges">
                <span class="badge">${formatBytes(item.fileSize)}</span>
                <span class="badge ready">Ready for Print</span>
            </div>
            <div class="asset-actions">
                <a class="action-chip" href="${item.previewUrl}" target="_blank" rel="noopener noreferrer">Открыть</a>
            </div>
        </div>`;

    return card;
}

function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(1024));
    return `${(bytes / (1024 ** i)).toFixed(1)} ${sizes[i]}`;
}

async function loadFiles() {
    const response = await fetch('/api/files');
    if (!response.ok) {
        return;
    }

    const files = await response.json();
    assetGrid.innerHTML = '';

    files.forEach((file, idx) => {
        const card = createAssetCard(file, idx);
        assetGrid.append(card);
    });

    document.querySelectorAll('.asset-preview').forEach((item) => {
        if (!item.querySelector('.asset-thumb')) {
            observer.observe(item);
        }
    });
}

function startScene(container) {
    if (sceneMap.has(container)) return;

    const width = container.clientWidth;
    const height = container.clientHeight;

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5));
    renderer.setSize(width, height);
    container.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(45, width / height, 0.1, 100);
    camera.position.set(0, 0, 4);

    const lightA = new THREE.HemisphereLight(0xffffff, 0x444444, 1.5); // Сделаем свет нейтральнее
    const lightB = new THREE.PointLight(0xffffff, 1);
    lightB.position.set(2, 2, 2);
    scene.add(lightA, lightB);

    let loadedModel; // Сюда сохраним загруженную модель
    let hoverSpin = false;

    // Инициализация загрузчика GLTF
    const fileUrl = container.dataset.url;

    // Проверяем, что это GLB (для STL и OBJ потребуются свои лоадеры: STLLoader / OBJLoader)
    if (fileUrl && fileUrl.toLowerCase().includes('.glb')) {
        const loader = new GLTFLoader();
        loader.load(fileUrl, function (gltf) {
            loadedModel = gltf.scene;

            // Центрирование и масштабирование модели (опционально, но полезно)
            const box = new THREE.Box3().setFromObject(loadedModel);
            const center = box.getCenter(new THREE.Vector3());
            loadedModel.position.sub(center); // Центрируем

            scene.add(loadedModel);
        }, undefined, function (error) {
            console.error('Ошибка загрузки GLB:', error);
        });
    } else {
        // Заглушка, если это не GLB (или пока не реализованы другие лоадеры)
        const geometry = new THREE.BoxGeometry(1, 1, 1);
        const material = new THREE.MeshStandardMaterial({ color: 0x8a2be2 });
        loadedModel = new THREE.Mesh(geometry, material);
        scene.add(loadedModel);
    }

    const skeleton = container.querySelector('.asset-skeleton');
    skeleton?.remove();

    const onEnter = () => hoverSpin = true;
    const onLeave = () => hoverSpin = false;
    container.addEventListener('mouseenter', onEnter);
    container.addEventListener('mouseleave', onLeave);

    let rafId = 0;
    const animate = () => {
        if (loadedModel) {
            loadedModel.rotation.y += hoverSpin ? 0.028 : 0.01;
            // Можно добавить вращение по X, если нужно
        }
        renderer.render(scene, camera);
        rafId = requestAnimationFrame(animate);
    };
    animate();

    sceneMap.set(container, {
        renderer, scene, rafId, onEnter, onLeave, model: loadedModel
    });
}

function stopScene(container) {
    const s = sceneMap.get(container);
    if (!s) return;

    cancelAnimationFrame(s.rafId);
    container.removeEventListener('mouseenter', s.onEnter);
    container.removeEventListener('mouseleave', s.onLeave);
    s.geometry.dispose();
    s.material.dispose();
    s.renderer.dispose();
    s.renderer.domElement.remove();

    sceneMap.delete(container);
}

const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
        const node = entry.target;
        if (entry.isIntersecting) {
            startScene(node);
        } else {
            stopScene(node);
        }
    });
}, { threshold: 0.25 });

window.addEventListener('beforeunload', () => {
    sceneMap.forEach((_, key) => stopScene(key));
});

loadFiles();
