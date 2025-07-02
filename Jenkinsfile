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
                echo "--------------- Déploiement du conteneur ---------------"
                script {
                    // Arrêt et suppression sécurisés de l'ancien conteneur
                    sh 'docker stop transfert_server || true'
                    sh 'docker rm transfert_server || true'
                    
                    sh "docker run -d -p 33333:80 --name=transfert_server transfer_multi_srv:latest"
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
