export function registerEventListeners(dotNetObj) {
    const keyDownHandler = (e) => {
        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            dotNetObj.invokeMethodAsync('CompileAndRenderAsync');
        }
    };

    document.addEventListener('keydown', keyDownHandler);

    return () => {
        document.removeEventListener('keydown', keyDownHandler);
    };
}
