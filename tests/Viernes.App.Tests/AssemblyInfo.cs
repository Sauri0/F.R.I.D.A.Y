using Xunit;

// El tamaño del orbe es una sola cosa para todo el proceso —hay una ventana, un orbe y una
// preferencia—, así que ShellLayout.Scale es estático. Con las colecciones en paralelo, una prueba
// que lo mueve a 200 % le cambia la geometría por debajo a otra que está midiendo bordes, y el
// resultado son fallas que aparecen y desaparecen según en qué orden arrancaron los hilos.
// Viernes.Core.Tests ya corre así por el mismo tipo de razón.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
