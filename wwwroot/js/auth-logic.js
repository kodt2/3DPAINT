function setStatus(message, type = '') {
    const node = document.getElementById('statusMessage');
    if (!node) {
        return;
    }

    node.textContent = message;
    node.classList.remove('error', 'success');
    if (type) {
        node.classList.add(type);
    }
}

function setLoading(button, loading) {
    if (!button) {
        return;
    }

    button.classList.toggle('loading', loading);
    button.disabled = loading;
}

async function parseResponseSafe(response) {
    const contentType = response.headers.get('content-type') || '';
    if (contentType.includes('application/json')) {
        try {
            return await response.json();
        } catch {
            return {};
        }
    }

    const text = await response.text();
    return { success: response.ok, message: text };
}

function updateRegisterVisualState() {
    const visuals = window.authVisuals;
    if (!visuals) {
        return;
    }

    const fullName = document.querySelector('#registerForm input[name="FullName"]')?.value?.trim();
    const birthDate = document.getElementById('regBirthDate')?.value?.trim();
    const login = document.querySelector('#registerForm input[name="Login"]')?.value?.trim();
    const password = document.getElementById('regPassword')?.value ?? '';

    visuals.visualState.fullName = Boolean(fullName);
    visuals.visualState.birthDate = Boolean(birthDate);
    visuals.visualState.login = Boolean(login);
    visuals.visualState.passwordReady = password.length >= 6;
    visuals.syncVisualProgress();
}

function animateHeroText(titleNode, subtitleNode, nextTitle, nextSubtitle) {
    const nodes = [titleNode, subtitleNode].filter(Boolean);
    if (nodes.length === 0) {
        return;
    }

    nodes.forEach((node) => node.classList.add('is-switching'));

    window.setTimeout(() => {
        if (titleNode) {
            titleNode.textContent = nextTitle;
        }
        if (subtitleNode) {
            subtitleNode.textContent = nextSubtitle;
        }

        requestAnimationFrame(() => {
            nodes.forEach((node) => node.classList.remove('is-switching'));
        });
    }, 180);
}

async function submitWithJson(form, endpoint, onSuccess) {
    if (!form) {
        return;
    }

    if (!form.checkValidity()) {
        form.reportValidity();
        return;
    }

    const submitButton = form.querySelector('.submit-btn');
    if (submitButton?.disabled) {
        return;
    }

    const formData = new FormData(form);

    setLoading(submitButton, true);
    setStatus('Обработка запроса...');

    try {
        const response = await fetch(endpoint, { method: 'POST', body: formData });
        const result = await parseResponseSafe(response);

        if (!response.ok || !result.success) {
            const errorText = result.message || (result.errors ? result.errors.join('\n') : 'Ошибка операции.');
            setStatus(errorText, 'error');
            return;
        }

        setStatus(result.message || 'Успешно.', 'success');
        onSuccess(result);
    } catch (error) {
        console.error('Auth request failed:', error);
        setStatus('Сервер недоступен. Повторите попытку позже.', 'error');
    } finally {
        setLoading(submitButton, false);
    }
}

function switchTab(mode) {
    const visuals = window.authVisuals;
    const authBox = document.getElementById('authBox');
    const pill = document.getElementById('togglePill');
    const loginForm = document.getElementById('loginForm');
    const registerForm = document.getElementById('registerForm');
    const title = document.getElementById('dynamic-title');
    const subtitle = document.getElementById('dynamic-subtitle');
    const buttons = document.querySelectorAll('.toggle-btn');

    if (!pill || !loginForm || !registerForm) {
        return;
    }

    buttons.forEach((btn) => {
        btn.classList.toggle('active', btn.dataset.mode === mode);
    });

    if (mode === 'login') {
        pill.style.transform = 'translateX(0)';
        registerForm.classList.add('hidden');
        loginForm.classList.remove('hidden');
        animateHeroText(
            title,
            subtitle,
            'С возвращением в Студию',
            'Открой проекты, ассеты и рабочие сцены за пару секунд.'
        );
    } else {
        pill.style.transform = 'translateX(100%)';
        loginForm.classList.add('hidden');
        registerForm.classList.remove('hidden');
        animateHeroText(
            title,
            subtitle,
            'Твоя Вселенная 3D‑ресурсов',
            'Зарегистрируйся и создай свой первый мир в 3D'
        );
    }

    authBox?.classList.add('flipping');
    window.setTimeout(() => authBox?.classList.remove('flipping'), 680);

    if (visuals) {
        visuals.visualState.mode = mode;
        visuals.syncVisualProgress();
    }

    setStatus('');
}

window.switchTab = switchTab;

document.addEventListener('DOMContentLoaded', () => {
    const loginForm = document.getElementById('loginForm');
    const registerForm = document.getElementById('registerForm');

    loginForm?.addEventListener('submit', async (event) => {
        event.preventDefault();

        await submitWithJson(loginForm, '/Account/Login', (result) => {
            window.location.href = result.redirectUrl;
        });
    });

    registerForm?.addEventListener('submit', async (event) => {
        event.preventDefault();

        await submitWithJson(registerForm, '/Account/Register', () => {
            switchTab('login');
            registerForm.reset();
            updateRegisterVisualState();
        });
    });

    registerForm?.querySelectorAll('input').forEach((input) => {
        input.addEventListener('input', updateRegisterVisualState);
        input.addEventListener('change', updateRegisterVisualState);
    });

    updateRegisterVisualState();
});
