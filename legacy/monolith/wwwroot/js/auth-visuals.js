import * as THREE from '/lib/three/three.module.js';
import { GLTFLoader } from '/lib/three/GLTFLoader.js';

let scene;
let camera;
let renderer;
let model;
let keyLight;
let fillLight;
let rimLight;
let pointerX = 0;
let pointerY = 0;
let targetBlur = 0;
let baseModelX = -1.85;
let baseModelY = 0;
let modelPivot;
let animationFrameId = 0;
let isInitialized = false;
let isAnimationRunning = false;

const visualState = {
    mode: 'login',
    fullName: false,
    birthDate: false,
    login: false,
    passwordReady: false,
    hoverForm: false
};

function init3D() {
    if (isInitialized) {
        return;
    }

    const canvas = document.getElementById('bgCanvas');
    if (!canvas) {
        return;
    }

    scene = new THREE.Scene();
    camera = new THREE.PerspectiveCamera(55, window.innerWidth / window.innerHeight, 0.1, 100);
    camera.position.set(0, 0, 7.3);

    try {
        renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, powerPreference: 'high-performance' });
    } catch (error) {
        console.error('WebGL renderer init failed:', error);
        return;
    }
    isInitialized = true;

    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

    modelPivot = new THREE.Group();
    scene.add(modelPivot);
    createFallbackModel();
    loadCustomModel();
    updateModelAnchor();

    keyLight = new THREE.PointLight(0xa358e8, 100, 25);
    keyLight.position.set(4, 3, 5);
    scene.add(keyLight);

    keyLight = new THREE.PointLight(0xa358e8, 100, 25);
    keyLight.position.set(4, 3, 3);
    scene.add(keyLight);

    keyLight = new THREE.PointLight(0xa358e8, 100, 25);
    keyLight.position.set(4, 3, 7);
    scene.add(keyLight);

    fillLight = new THREE.PointLight(0xf0f0ff, 50, 25);
    fillLight.position.set(-4, -2, 4);
    scene.add(fillLight);

    rimLight = new THREE.DirectionalLight(0xffffff, 0.45);
    rimLight.position.set(-2, 1, -3);
    scene.add(rimLight);

    const ambient = new THREE.AmbientLight(0xffffff, 0.25);
    scene.add(ambient);

    bindVisualEvents();
    animate();
}

function bindVisualEvents() {
    document.addEventListener('pointermove', (event) => {
        pointerX = (event.clientX / window.innerWidth) * 2 - 1;
        pointerY = -((event.clientY / window.innerHeight) * 2 - 1);
    }, { passive: true });

    const authBox = document.getElementById('authBox');
    if (authBox) {
        authBox.addEventListener('mouseenter', () => {
            visualState.hoverForm = true;
            targetBlur = 0.35;
        });
        authBox.addEventListener('mouseleave', () => {
            visualState.hoverForm = false;
            targetBlur = 0;
        });
    }

    renderer.domElement.addEventListener('webglcontextlost', (event) => {
        event.preventDefault();
        cancelAnimationFrame(animationFrameId);
        animationFrameId = 0;
        isAnimationRunning = false;
    });

    renderer.domElement.addEventListener('webglcontextrestored', () => {
        animate();
    });

    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = 0;
            isAnimationRunning = false;
            return;
        }

        if (!isAnimationRunning) {
            animate();
        }
    });
}

function syncVisualProgress() {
    if (!model) {
        return;
    }

    // Вспомогательные функции для управления цветом освещения
    const setLightsToWhite = () => {
        keyLight.color.setHex(0xffffff);
        fillLight.color.setHex(0xffffff);
    };

    const setLightsToColor = () => {
        keyLight.color.setHex(0xa358e8); // Пурпурный (как было изначально)
        fillLight.color.setHex(0xf0f0ff); // Холодный белый/голубоватый
    };

    // --- 1. Режим "Вход" ---
    // Идеальная 3Д модель (финальный "отрендеренный" результат)
    if (visualState.mode === 'login') {
        applyMaterialState({ wireframe: false, color: 0xd8dbef, roughness: 0.22, metalness: 0.9, emissive: 0x1f0a3f });
        setLightsToColor();
        keyLight.intensity = 36;
        fillLight.intensity = 32;
        return;
    }

    // --- 2. Режим "Регистрация" ---

    // Считаем прогресс заполнения формы (от 0 до 4)
    let filledFieldsCount = 0;
    if (visualState.fullName) filledFieldsCount++;
    if (visualState.birthDate) filledFieldsCount++;
    if (visualState.login) filledFieldsCount++;
    if (visualState.passwordReady) filledFieldsCount++;

    switch (filledFieldsCount) {
        case 0:
            // Всё пусто: модель из ребер голубого неонового цвета, свет весь белый
            applyMaterialState({ wireframe: true, color: 0x00f0ff, roughness: 1.0, metalness: 0.0, emissive: 0x00f0ff });
            setLightsToWhite();
            keyLight.intensity = 20;
            fillLight.intensity = 15;
            break;

        case 1:
            // Заполнено 1 поле: "неотрендеренная" серая модель, полностью матовая, белый свет.
            // *Примечание: в базовом Three.js нельзя одновременно сделать модель сплошной и наложить сетку 
            // без дублирования геометрии, поэтому мы имитируем "черновую" модель сплошным матовым серым цветом (как глина).
            applyMaterialState({ wireframe: false, color: 0x7a7a7a, roughness: 1.0, metalness: 0.0, emissive: 0x000000 });
            setLightsToWhite();
            keyLight.intensity = 25;
            fillLight.intensity = 20;
            break;

        case 2:
            // Заполнено 2 поля: модель красится, всё полностью матовое, белый свет
            applyMaterialState({ wireframe: false, color: 0x3498db, roughness: 1.0, metalness: 0.0, emissive: 0x042730 });
            setLightsToWhite();
            keyLight.intensity = 25;
            fillLight.intensity = 20;
            break;

        case 3:
            // Заполнено 3 поля: появляется цветной свет (материал модели всё ещё матовый)
            applyMaterialState({ wireframe: false, color: 0x3498db, roughness: 1.0, metalness: 0.0, emissive: 0x042730 });
            setLightsToColor();
            keyLight.intensity = 30;
            fillLight.intensity = 25;
            break;

        case 4:
            // Заполнены все 4 поля: идеальная 3D модель (появляется металличность, блики)
            applyMaterialState({ wireframe: false, color: 0xd8dbef, roughness: 0.22, metalness: 0.9, emissive: 0x1f0a3f });
            setLightsToColor();
            keyLight.intensity = 36;
            fillLight.intensity = 32;
            break;
    }
}

function animate() {
    if (isAnimationRunning && animationFrameId) {
        return;
    }
    isAnimationRunning = true;

    const renderFrame = () => {
        animationFrameId = requestAnimationFrame(renderFrame);

        if (!renderer || !model) {
            return;
        }

        model.rotation.y += 0.001;
        //model.rotation.x += 0.0015;

        //model.rotation.y += pointerX * 0.0007;
        //model.rotation.x += pointerY * 0.0005;
        modelPivot.position.x += ((baseModelX + pointerX * 0.22) - modelPivot.position.x) * 0.06;
        modelPivot.position.y += ((baseModelY + pointerY * 0.18) - modelPivot.position.y) * 0.06;

        keyLight.position.x = 4 + pointerX * 2;
        keyLight.position.y = 3 + pointerY * 1.5;
        fillLight.position.x = -4 - pointerX * 2;
        rimLight.position.y = 1 + pointerY * 0.7;

        const blurPulse = targetBlur * (0.8 + Math.sin(Date.now() * 0.002) * 0.08);
        renderer.domElement.style.filter = blurPulse > 0 ? `blur(${blurPulse}px)` : 'none';

        renderer.render(scene, camera);
    };

    renderFrame();
}

function updateModelAnchor() {

    const leftZoneCenterNdcX = -0.4;
    const distance = camera?.position?.z ?? 7.3;
    const halfFovRad = THREE.MathUtils.degToRad((camera?.fov ?? 55) / 2);
    const viewHalfWidth = Math.tan(halfFovRad) * distance * (camera?.aspect ?? (window.innerWidth / window.innerHeight));

    baseModelX = leftZoneCenterNdcX * viewHalfWidth;

    baseModelY = -1.8; // 👈 СДВИГ ВНИЗ (главное)
}

function createFallbackModel() {
    const geometry = new THREE.IcosahedronGeometry(1.8, 4);
    const material = new THREE.MeshStandardMaterial({
        color: 0x00f0ff,
        roughness: 0.7,
        metalness: 0.15,
        wireframe: true,
        emissive: 0x042730,
        emissiveIntensity: 0.7
    });

    model = new THREE.Mesh(geometry, material);
    modelPivot.add(model);
}

function loadCustomModel() {
    const loader = new GLTFLoader();
    loader.load(
        '/models/auth-object.glb',
        (gltf) => {
            const loadedModel = gltf.scene;
            loadedModel.scale.setScalar(10);
            loadedModel.traverse((child) => {
                if (!child.isMesh) {
                    return;
                }

                child.castShadow = false;
                child.receiveShadow = false;

                if (!child.material || Array.isArray(child.material)) {
                    return;
                }

                child.material = child.material.clone();
                child.material.emissive = new THREE.Color(0x042730);
                child.material.emissiveIntensity = 0.45;
            });

            modelPivot.remove(model);
            model = loadedModel;
            modelPivot.add(model);
            syncVisualProgress();
        },
        undefined,
        (error) => {
            console.warn('Custom GLB model was not loaded, using fallback geometry.', error);
        }
    );
}

function applyMaterialState(nextState) {
    model.traverse?.((child) => {
        if (!child.isMesh || !child.material || Array.isArray(child.material)) {
            return;
        }

        const material = child.material;
        if ('wireframe' in material) material.wireframe = nextState.wireframe;
        if ('color' in material) material.color.setHex(nextState.color);
        if ('roughness' in material) material.roughness = nextState.roughness;
        if ('metalness' in material) material.metalness = nextState.metalness;
        if ('emissive' in material) material.emissive.setHex(nextState.emissive);
        material.needsUpdate = true;
    });
}

window.addEventListener('resize', () => {
    if (!camera || !renderer) {
        return;
    }

    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
    updateModelAnchor();
});

window.authVisuals = {
    init3D,
    visualState,
    syncVisualProgress
};

document.addEventListener('DOMContentLoaded', init3D);