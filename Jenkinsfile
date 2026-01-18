pipeline {
    agent any

    environment {
        PROJECT_DIR = 'TransferMulti.srv'
    }

    stages {
        stage('Checkout Code') {
            steps {
                echo "--------------- Récupération du code ---------------"
                checkout scm  // Récupère le code du contrôle de source Jenkins
                dir(env.PROJECT_DIR) {
                    // Maintenant dans le contexte du dépôt Git
                    script {
                        GITHASH = sh(
                            script: 'git rev-parse --short HEAD',
                            returnStdout: true
                        ).trim()
                    }
                    echo "Git Hash: ${GITHASH}"
                }
            }
        }

        stage('Build Docker Image') {
            steps {
                echo "--------------- Construction de l'image Docker ---------------"
                dir(env.PROJECT_DIR) {
                    sh "docker build -t transfer_multi_srv:${GITHASH} ."
                    sh "docker tag transfer_multi_srv:${GITHASH} transfer_multi_srv:latest"
                }
            }
        }

        stage('Deploy Container') {
            steps {
                echo "--------------- Déploiement du conteneur (Blue/Green) ---------------"
                script {
                    def IMAGE = "transfer_multi_srv:latest"
                    def CONTAINER_PREFIX = "transfert_server"
                    def PORT_RANGE = (5000..5003)
                    def HEALTH_PATH = "/actuator/health"

                    def newPort = null
                    def oldContainer = null
                    def newContainer = null

                    echo "🔍 Recherche d'un port disponible (5000-5003)"

                    for (p in PORT_RANGE) {
                        def result = sh(
                            script: """
                                docker ps --format '{{.Names}} {{.Ports}}' | grep -w '${p}' || true
                            """,
                            returnStdout: true
                        ).trim()

                        if (!result) {
                            newPort = p
                            break
                        } else {
                            oldContainer = result.split(" ")[0]
                        }
                    }

                    if (newPort == null) {
                        error "❌ Tous les ports 5000-5003 sont occupés"
                    }

                    newContainer = "${CONTAINER_PREFIX}_${newPort}"

                    echo "🚀 Démarrage du nouveau conteneur ${newContainer} sur le port ${newPort}"

                    sh """
                        docker run -d \
                        --name ${newContainer} \
                        -p ${newPort}:80 \
                        ${IMAGE}
                    """

                    echo "⏳ Vérification de l'état de santé..."

                    def healthy = false
                    for (int i = 0; i < 10; i++) {
                        def health = sh(
                            script: """
                                curl -sf http://localhost:${newPort}${HEALTH_PATH} | grep 'UP' || true
                            """,
                            returnStdout: true
                        ).trim()

                        if (health) {
                            healthy = true
                            break
                        }
                        sleep 3
                    }

                    if (!healthy) {
                        echo "❌ Échec du health check – rollback"
                        sh "docker rm -f ${newContainer}"
                        error "Déploiement annulé, ancien conteneur conservé"
                    }

                    echo "✅ Health check OK"

                    if (oldContainer) {
                        echo "🧹 Suppression de l'ancien conteneur ${oldContainer}"
                        sh "docker rm -f ${oldContainer}"
                    } else {
                        echo "ℹ️ Aucun ancien conteneur à supprimer"
                    }

                    echo "🎉 Déploiement réussi sur le port ${newPort}"
                }
            }
        }


        stage('Cleanup') {
            steps {
                echo "--------------- Nettoyage des ressources ---------------"
                sh 'docker image prune -f'
            }
        }
    }

    post {
        always {
            echo "--------------- État final des images Docker ---------------"
            sh 'docker images'
        }
    }
}
